using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using Libplanet;
using Libplanet.Crypto;
using Libplanet.Net;
using Nekoyume.State;
using Nekoyume.Helper;
using NetMQ;
using UnityEngine;
using UnityEngine.UI;

namespace Nekoyume.BlockChain
{
    /// <summary>
    /// Agent를 구동시킨다.
    /// </summary>
    public class AgentController : MonoSingleton<AgentController>
    {
        public const string PlayerPrefsKeyOfAgentPrivateKey = "private_key_agent";
#if UNITY_EDITOR
        private const string AgentStoreDirName = "ssglime_dev";
#else
        private const string AgentStoreDirName = "ssglime";
#endif

#if NEKOALPHA_NOMINER
        private static readonly string CommandLineOptionsJsonPath = Path.Combine(Application.streamingAssetsPath, "clo_nekoalpha_nominer.json");
#else
        private static readonly string CommandLineOptionsJsonPath = Path.Combine(Application.streamingAssetsPath, "clo.json");
#endif
        private const string PeersFileName = "peers.dat";
        private const string IceServersFileName = "ice_servers.dat";

        private static readonly string DefaultStoragePath =
            Path.Combine(Application.persistentDataPath, AgentStoreDirName);

        public static Agent Agent { get; private set; }

        private static IEnumerator _miner;
        private static IEnumerator _txProcessor;
        private static IEnumerator _swarmRunner;
        private static IEnumerator _logger;

        private Text statusText;
        private EventHandler loadEndedHandler;

        public static void Initialize(Action<bool> callback, Text textField, EventHandler handler)
        {
            if (!ReferenceEquals(Agent, null))
            {
                return;
            }

            instance.statusText = textField;
            instance.loadEndedHandler += handler;
            instance.InitAgent(callback);
        }

        private void InitAgent(Action<bool> callback)
        {
            var options = GetOptions(CommandLineOptionsJsonPath);
            var privateKey = GetPrivateKey(options);
            var peers = GetPeers(options);
            var iceServers = GetIceServers();
            var host = GetHost(options);
            int? port = options.Port;
            var storagePath = options.StoragePath ?? DefaultStoragePath;

            Agent = new Agent(
                privateKey: privateKey,
                path: storagePath,
                peers: peers,
                iceServers: iceServers,
                host: host,
                port: port
            );

            // 별도 쓰레드에서는 GameObject.GetComponent<T> 를 사용할 수 없기때문에 미리 선언.
            Agent.BootstrapStarted += (_, __) => { statusText.text = "네트워크 연결을 수립하고 있습니다..."; };
            Agent.PreloadProcessed += (_, state) =>
            {
                string text;

                switch (state)
                {
                    case BlockDownloadState blockDownloadState:
                        text = "블록 다운로드 중... " + $"{blockDownloadState.ReceivedBlockCount} / {blockDownloadState.TotalBlockCount}";
                        break;

                    case StateReferenceDownloadState stateReferenceDownloadState:
                        text = "상태 다운로드 중... " +
                            $"{stateReferenceDownloadState.ReceivedStateReferenceCount} / {stateReferenceDownloadState.TotalStateReferenceCount}";
                        break;

                    case BlockStateDownloadState blockStateDownloadState:
                        text =
                            $"{blockStateDownloadState.ReceivedBlockStateCount} / {blockStateDownloadState.TotalBlockStateCount}";
                        break;

                    case ActionExecutionState actionExecutionState:
                        text =
                            $"{actionExecutionState.ExecutedBlockCount} / {actionExecutionState.TotalBlockCount}";
                        break;

                    default:
                        throw new Exception("Unknown state was reported during preload.");
                }

                statusText.text = $"{text}  ({state.CurrentPhase} / {PreloadState.TotalPhase})";
            };
            Agent.PreloadEnded += (_, __) =>
            {
                // 에이전트의 준비단계가 끝나면 에이전트의 상태를 한 번 동기화 한다.
                //States.Instance.agentState = (AgentState) Agent.GetState(Agent.Address) ??
                //                                   new AgentState(Agent.Address);
                // 에이전트에 포함된 모든 아바타의 상태를 한 번씩 동기화 한다.
                /*foreach (var pair in States.Instance.agentState.Value.avatarAddresses)
                {
                    var avatarState = (AvatarState) Agent.GetState(pair.Value);
                    States.Instance.avatarStates.Add(pair.Key, avatarState);
                }*/

                // 그리고 모든 액션에 대한 랜더를 핸들링하기 시작한다.
                ActionRenderHandler.Instance.Start();
                // 그리고 마이닝을 시작한다.
                StartNullableCoroutine(_miner);
                StartCoroutine(CoCheckBlockTip());

                callback(Agent.SyncSucceed);
                Agent.LoadQueuedActions();
                loadEndedHandler.Invoke(this, null);
            };
            _miner = options.NoMiner ? null : Agent.CoMiner();

            StartSystemCoroutines(Agent);
        }

        private static IEnumerator CoCheckBlockTip()
        {
            while (true)
            {
                var current = Agent.BlockIndex;
                yield return new WaitForSeconds(180f);
                if (Agent.BlockIndex == current)
                {
                    break;
                }
            }
        }

        public static CommandLineOptions GetOptions(string jsonPath)
        {
            if (File.Exists(jsonPath))
            {
                return JsonUtility.FromJson<CommandLineOptions>(
                    File.ReadAllText(jsonPath)
                );
            }
            else 
            {
                return CommnadLineParser.GetCommandLineOptions() ?? new CommandLineOptions();
            }
        }

        private static PrivateKey GetPrivateKey(CommandLineOptions options)
        {
            PrivateKey privateKey;
            var privateKeyHex = options.PrivateKey ?? PlayerPrefs.GetString(PlayerPrefsKeyOfAgentPrivateKey, "");

            if (string.IsNullOrEmpty(privateKeyHex))
            {
                privateKey = new PrivateKey();
                PlayerPrefs.SetString(PlayerPrefsKeyOfAgentPrivateKey, ByteUtil.Hex(privateKey.ByteArray));
            }
            else
            {
                privateKey = new PrivateKey(ByteUtil.ParseHex(privateKeyHex));
            }

            return privateKey;
        }

        private static IEnumerable<Peer> GetPeers(CommandLineOptions options)
        {
            return options.Peers?.Any() ?? false
                ? options.Peers.Select(LoadPeer)
                : LoadConfigLines(PeersFileName).Select(LoadPeer);
        }

        private static IEnumerable<IceServer> GetIceServers()
        {
            return LoadIceServers();
        }

        private static string GetHost(CommandLineOptions options)
        {
            return options.Host;
        }

        private static BoundPeer LoadPeer(string peerInfo)
        {
            string[] tokens = peerInfo.Split(',');
            var pubKey = new PublicKey(ByteUtil.ParseHex(tokens[0]));
            string host = tokens[1];
            int port = int.Parse(tokens[2]);

            return new BoundPeer(pubKey, new DnsEndPoint(host, port), 0);
        }

        private static IEnumerable<string> LoadConfigLines(string fileName)
        {
            string userPath = Path.Combine(
                Application.persistentDataPath,
                fileName
            );
            string content;
            
            if (File.Exists(userPath))
            {
                content = File.ReadAllText(userPath);
            }
            else 
            {
                string assetName = Path.GetFileNameWithoutExtension(fileName);
                content = Resources.Load<TextAsset>($"Config/{assetName}").text;
            }

            foreach (var line in Regex.Split(content, "\n|\r|\r\n"))
            {
                if (!string.IsNullOrEmpty(line.Trim()))
                {
                    yield return line;
                }
            }
        }

        private static IEnumerable<IceServer> LoadIceServers()
        {
            foreach (string line in LoadConfigLines(IceServersFileName)) 
            {
                var uri = new Uri(line);
                string[] userInfo = uri.UserInfo.Split(':');

                yield return new IceServer(new[] {uri}, userInfo[0], userInfo[1]);
            }
        }

        #region Mono

        protected override void OnDestroy()
        {
            if(!instance.Equals(this))
            {
                return;
            }

            Debug.LogError("Destroyed");

            ActionRenderHandler.Instance.Stop();
            Agent?.Dispose();
            
            NetMQConfig.Cleanup(false);

            base.OnDestroy();
        }

        #endregion

        private void StartSystemCoroutines(Agent agent)
        {
            _txProcessor = agent.CoTxProcessor();
            _swarmRunner = agent.CoSwarmRunner();
#if DEBUG
            _logger = agent.CoLogger();
#endif

            StartNullableCoroutine(_txProcessor);
            StartNullableCoroutine(_swarmRunner);
            StartNullableCoroutine(_logger);
        }

        private Coroutine StartNullableCoroutine(IEnumerator routine)
        {
            return ReferenceEquals(routine, null) ? null : StartCoroutine(routine);
        }
    }
}
