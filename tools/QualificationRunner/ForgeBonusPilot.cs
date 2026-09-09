using System;
using System.Collections;
using HarmonyLib;

namespace VGModAPI.Qualification;

public sealed partial class Plugin
{
    // Only the synchronous native completion runs under this disposable skill fixture.
    // It qualifies the bonus branch, not earned skill progression or natural bonus rates.
    private void CompleteGuaranteedForgeBonus(object job, float duration)
    {
        var node = SpGet(NativeType("Behaviour.Crew.SkilltreeNode"), "industrialForgeBonusCraft")!;
        var tree = SpCall(SpGet(CurrentPlayer, "commander")!, "GetSkillTreeData", SpGet(node, "parent")!, false);
        Require(tree != null, "Bonus fixture requires an existing Industrial skill tree.");
        var nodes = (IDictionary)SpGet(tree!, "nodes")!;
        var key = (string)SpGet(node, "identifier")!;
        var existed = nodes.Contains(key); var previous = nodes[key];
        var field = AccessTools.Field(node.GetType(), "customIncrease");
        var increase = (float)field.GetValue(node);
        var replacement = Activator.CreateInstance(NativeType("Source.Personnel.SkillNodeData"), tree, node)!;
        AccessTools.Property(replacement.GetType(), "currentPoints").SetValue(replacement, 1);
        try
        {
            nodes[key] = replacement;
            var points = Convert.ToInt32(SpGet(node, "currentPoints"));
            Require(points > 0, "Bonus fixture has no effective skill point.");
            field.SetValue(node, 1f / points);
            Require(Convert.ToSingle(SpGet(node, "currentIncrease")) == 1f, "Bonus fixture must have probability exactly one.");
            SpCall(job, "ProgressJob", duration);
        }
        finally
        {
            field.SetValue(node, increase);
            if (existed) nodes[key] = previous; else nodes.Remove(key);
        }
        Require((float)field.GetValue(node) == increase && nodes.Contains(key) == existed
            && (!existed || ReferenceEquals(nodes[key], previous)), "Bonus fixture was not restored exactly.");
    }
}
