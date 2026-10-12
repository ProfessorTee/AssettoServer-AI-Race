using FluentValidation;
using JetBrains.Annotations;

namespace BotDriverPlugin;

[UsedImplicitly]
public class BotDriverConfigurationValidator : AbstractValidator<BotDriverConfiguration>
{
    public BotDriverConfigurationValidator()
    {
        RuleFor(cfg => cfg.AiStrength).InclusiveBetween(50, 110);
        RuleFor(cfg => cfg.AiStrengthSpread).InclusiveBetween(0, 30);
        RuleFor(cfg => cfg.AiAggression).InclusiveBetween(0, 100);
        RuleFor(cfg => cfg.AiAggressionVariation).InclusiveBetween(0, 100);
        RuleFor(cfg => cfg.PhysicsCorneringGrip).InclusiveBetween(0.8f, 1.5f);
        RuleFor(cfg => cfg.TyreWearFactor).InclusiveBetween(0, 10);
        RuleFor(cfg => cfg.BotDamageFactor).InclusiveBetween(0, 10);
        RuleFor(cfg => cfg.HighBeamRange).InclusiveBetween(10, 1000);
        RuleFor(cfg => cfg.GhostAfterSeconds).Must(x => x == 0 || x is >= 10 and <= 300).WithMessage("GhostAfterSeconds: 0 or 10-300");
        RuleFor(cfg => cfg.QualifyingBotDelaySeconds).InclusiveBetween(0, 600);
        RuleFor(cfg => cfg.PracticeBotDelaySeconds).InclusiveBetween(0, 600);
        RuleFor(cfg => cfg.PitReleaseIntervalSeconds).InclusiveBetween(1, 120);
        RuleFor(cfg => cfg.ChatLanguage).Must(x => x is null or "" or "de" or "en").WithMessage("ChatLanguage: de or en");
        RuleFor(cfg => cfg.RainGripLoss).InclusiveBetween(0, 3);
        RuleFor(cfg => cfg.PitSpeedKmh).InclusiveBetween(20, 200);
        RuleFor(cfg => cfg.CoolDownPace).InclusiveBetween(0.2f, 1);
        RuleForEach(cfg => cfg.BotSlots).GreaterThanOrEqualTo(0);
        RuleForEach(cfg => cfg.Drivers).ChildRules(d =>
        {
            d.RuleFor(x => x.Slot).GreaterThanOrEqualTo(0);
            d.RuleFor(x => x.Strength).InclusiveBetween(50, 110).When(x => x.Strength.HasValue);
            d.RuleFor(x => x.Aggression).InclusiveBetween(0, 100).When(x => x.Aggression.HasValue);
        });
    }
}
