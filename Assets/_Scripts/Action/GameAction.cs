using System;
using System.Collections.Immutable;

namespace Nekoyume.Action
{
    [Serializable]
    public abstract class GameAction : ActionBase
    {
        public Guid Id { get; private set; }
        public override IImmutableDictionary<string, object> PlainValue => PlainValueInternal.SetItem("id", Id.ToString());
        protected abstract IImmutableDictionary<string, object> PlainValueInternal { get; }
        
        protected GameAction()
        {
            Id = Guid.NewGuid();
        }

        public override void LoadPlainValue(IImmutableDictionary<string, object> plainValue)
        {
            Id = new Guid((string) plainValue["id"]);
            LoadPlainValueInternal(plainValue);
        }
        
        protected abstract void LoadPlainValueInternal(IImmutableDictionary<string, object> plainValue);
    }
}
