using FluentValidation;
using JetBrains.Annotations;

namespace RaceAiPlugin;

[UsedImplicitly]
public class RaceAiConfigurationValidator : AbstractValidator<RaceAiConfiguration>
{
    public RaceAiConfigurationValidator()
    {
        RuleFor(cfg => cfg.AiLevel).InclusiveBetween(0, 100);
        RuleFor(cfg => cfg.AiLevelVariation).InclusiveBetween(0, 100);
        RuleFor(cfg => cfg.AiAggression).InclusiveBetween(0, 100);
        RuleFor(cfg => cfg.AiAggressionVariation).InclusiveBetween(0, 100);
        RuleFor(cfg => cfg.SlipstreamStrength).InclusiveBetween(0, 1);
        RuleFor(cfg => cfg.CoolDownPace).InclusiveBetween(0.2f, 1);
        RuleFor(cfg => cfg.EdgeMargin).InclusiveBetween(-1, 3);
        RuleFor(cfg => cfg.SideMargin).InclusiveBetween(0, 3);
        RuleForEach(cfg => cfg.BotSlots).GreaterThanOrEqualTo(0);
        RuleForEach(cfg => cfg.Drivers).ChildRules(d =>
        {
            d.RuleFor(x => x.Slot).GreaterThanOrEqualTo(0);
            d.RuleFor(x => x.Level).InclusiveBetween(0, 100).When(x => x.Level.HasValue);
            d.RuleFor(x => x.Aggression).InclusiveBetween(0, 100).When(x => x.Aggression.HasValue);
        });
    }
}
