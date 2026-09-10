namespace Source.Mining
{
    public sealed class SpriteBreakPoint { }
}
namespace Source.Data
{
    public class AbstractUnitData
    {
        public string guid { get; protected set; } = "unit";
        public float currentHullHP = -1f, currentArmorHP = -1f, currentShieldHP = -1f, empCharge;
        public System.Collections.Generic.List<Source.Mining.SpriteBreakPoint> battleDamage = new();
        public void SetTestGuid(string value) => guid = value;
    }
}
namespace Behaviour.Weapons
{
    public abstract class TargetableUnit : UnityEngine.Object
    {
        public bool isInvincible;
        public bool isDestroyed { get; protected set; }
        public void SetTestDestroyed(bool value) => isDestroyed = value;
    }
}
namespace Behaviour.Unit
{
    public sealed class TestUnit : AbstractUnit { }
}
