using FluentValidation;
using JetBrains.Annotations;

namespace RaceAiPlugin;

[UsedImplicitly]
public class RaceAiConfigurationValidator : AbstractValidator<RaceAiConfiguration>
{
    public RaceAiConfigurationValidator()
    {
        RuleFor(cfg => cfg.AiStrength).InclusiveBetween(50, 110);
        RuleFor(cfg => cfg.AiStrengthSpread).InclusiveBetween(0, 30);
        RuleFor(cfg => cfg.TyreWearFactor).InclusiveBetween(0, 10);
        RuleFor(cfg => cfg.RainGripLoss).InclusiveBetween(0, 3);
        RuleFor(cfg => cfg.ImpatienceSeconds).InclusiveBetween(0, 600);
        RuleFor(cfg => cfg.RealWeatherUpdateMinutes).InclusiveBetween(1, 1440);
        RuleFor(cfg => cfg.RealWeatherTransitionSeconds).InclusiveBetween(1, 3600);
        RuleFor(cfg => cfg.TyreChangeGrip).InclusiveBetween(0.5f, 1);
        RuleFor(cfg => cfg.PitSpeedKmh).InclusiveBetween(20, 200);
        RuleFor(cfg => cfg.AiAggression).InclusiveBetween(0, 100);
        RuleFor(cfg => cfg.AiAggressionVariation).InclusiveBetween(0, 100);
        RuleFor(cfg => cfg.SlipstreamStrength).InclusiveBetween(0, 2.8f);
        RuleFor(cfg => cfg.CoolDownPace).InclusiveBetween(0.2f, 1);
        RuleFor(cfg => cfg.EdgeMargin).InclusiveBetween(-1, 3);
        RuleFor(cfg => cfg.SideMargin).InclusiveBetween(0, 3);
        RuleForEach(cfg => cfg.BotSlots).GreaterThanOrEqualTo(0);
        RuleForEach(cfg => cfg.Drivers).ChildRules(d =>
        {
            d.RuleFor(x => x.Slot).GreaterThanOrEqualTo(0);
            d.RuleFor(x => x.Strength).InclusiveBetween(50, 110).When(x => x.Strength.HasValue);
            d.RuleFor(x => x.Aggression).InclusiveBetween(0, 100).When(x => x.Aggression.HasValue);
        });
    }
}
