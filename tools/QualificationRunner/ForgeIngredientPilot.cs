using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using VGModAPI;

namespace VGModAPI.Qualification;

public sealed partial class Plugin
{
    // Bounded, single-level preparation using actual Forge jobs, never inserted inventory rows.
    private List<RecipeQuote>? GeneratedIngredientPlan(RecipeStationHandle station, RecipeQuote target)
    {
        if (target.Status != RecipeQuoteStatus.Available || target.Blockers.Any(b => b != RecipeBlocker.PricingUnavailable && b != RecipeBlocker.MissingIngredients)) return null;
        var plan = new List<RecipeQuote>();
        var catalog = ModApi.Services.Recipes.Read();
        foreach (var input in target.Inputs.Where(i => i.Missing > 0))
        {
            if (input.Resource.Kind != RecipeResourceKind.Item || plan.Count >= 8) return null;
            RecipeQuote? chosen = null;
            foreach (var producer in catalog.FindProducers(input.Resource).Where(p => p.Process == RecipeProcess.Forge))
            {
                var one = ModApi.Services.RecipeQuotes.Quote(station, producer.Id, 1);
                var output = one.Outputs.FirstOrDefault(o => o.Resource?.Equals(input.Resource) == true && o.ProbabilityPerBatch == 1);
                if (one.Status != RecipeQuoteStatus.Available || output == null) continue;
                var count = Math.Ceiling(input.Missing!.Value / output.Amount);
                if (count < 1 || count > 512) continue;
                var quote = ModApi.Services.RecipeQuotes.Quote(station, producer.Id, (int)count);
                if (quote.Status != RecipeQuoteStatus.Available || quote.Blockers.Any(b => b != RecipeBlocker.PricingUnavailable)
                    || quote.Inputs.Any(i => i.Resource.Kind != RecipeResourceKind.RefinedMaterial)) continue;
                chosen = quote; break;
            }
            if (chosen == null) return null;
            plan.Add(chosen);
        }
        // Shared raw stock must fund all preparatory jobs plus the final equipment job together.
        foreach (var group in plan.SelectMany(q => q.Inputs).Concat(target.Inputs.Where(i => i.Resource.Kind == RecipeResourceKind.RefinedMaterial)).GroupBy(i => i.Resource))
            if (group.Any(i => !i.Available.HasValue) || group.Sum(i => i.Required) > group.Min(i => i.Available!.Value)) return null;
        return plan;
    }
    private void PrepareGeneratedIngredients(RecipeStationHandle station, object forge, IList nativeJobs, IReadOnlyList<RecipeQuote> plan)
    {
        foreach (var quote in plan)
        {
            var oldJobs = nativeJobs.Cast<object>().ToArray();
            var queued = ModApi.Services.CraftingCommands.Execute(CraftingCommandRequest.Queue(Id, Guid.NewGuid(), station, quote.Recipe, quote.Batches, CraftingProtectionPolicy.NativeConsumption));
            Require(queued.Status == CraftingCommandStatus.Succeeded && queued.Jobs.Count == 1, "Generated ingredient preparation queue refused.");
            var job = nativeJobs.Cast<object>().Single(j => !oldJobs.Any(old => ReferenceEquals(old, j)));
            var duration = Convert.ToSingle(SpGet(job, "craftingTime"));
            Require(duration > 0 && !float.IsNaN(duration) && !float.IsInfinity(duration), "Ingredient job duration invalid.");
            // Drive individual batches to avoid changing vanilla float accumulation semantics.
            for (var batch = 0; batch < quote.Batches; batch++) SpCall(job, "ProgressJob", duration);
            Require(Convert.ToInt32(SpGet(job, "remainingAmount")) == 0, "Ingredient job did not complete all batches.");
            SpCall(forge, "ProgressJobs", 0f);
            Require(!nativeJobs.Contains(job), "Ingredient job was not retired.");
        }
    }
}
