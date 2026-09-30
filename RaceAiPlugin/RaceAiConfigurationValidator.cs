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
        RuleFor(cfg => cfg.HumanErrorsBelow).InclusiveBetween(0, 110);
        RuleFor(cfg => cfg.HumanErrorsFull).InclusiveBetween(0, 110);
        RuleFor(cfg => cfg.BotDamageFactor).InclusiveBetween(0, 10);
        RuleFor(cfg => cfg.HighBeamRange).InclusiveBetween(10, 1000);
        RuleFor(cfg => cfg.GhostAfterSeconds).Must(x => x == 0 || x is >= 10 and <= 300).WithMessage("GhostAfterSeconds: 0 or 10-300");
        RuleFor(cfg => cfg.UnstuckSeconds).InclusiveBetween(1, 60);
        RuleFor(cfg => cfg.QualifyingBotDelaySeconds).InclusiveBetween(0, 600);
        RuleFor(cfg => cfg.PracticeBotDelaySeconds).InclusiveBetween(0, 600);
        RuleFor(cfg => cfg.PitReleaseIntervalSeconds).InclusiveBetween(1, 120);
        RuleFor(cfg => cfg.ChatLanguage).Must(x => x is "de" or "en").WithMessage("ChatLanguage: de or en");
        RuleForEach(cfg => cfg.Personalities).ChildRules(p =>
        {
            p.RuleFor(x => x.Name).NotEmpty();
            p.RuleFor(x => x.Share).GreaterThanOrEqualTo(0);
            p.RuleFor(x => x.TyreWear).InclusiveBetween(0.2f, 3);
            p.RuleFor(x => x.FuelUse).InclusiveBetween(0.5f, 2);
            p.RuleFor(x => x.Mistakes).InclusiveBetween(0, 5);
            p.RuleFor(x => x.LineErrors).InclusiveBetween(0, 5);
            p.RuleFor(x => x.BrakeBehavior).InclusiveBetween(-1, 1);
            p.RuleFor(x => x.Patience).InclusiveBetween(0, 1);
            p.RuleFor(x => x.TyreChangeAt).InclusiveBetween(0, 100);
        });
        RuleFor(cfg => cfg.RainGripLoss).InclusiveBetween(0, 3);
        RuleFor(cfg => cfg.ImpatienceSeconds).InclusiveBetween(0, 600);
        RuleFor(cfg => cfg.FlashStartDelaySeconds).InclusiveBetween(0, 3600);
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
        RuleFor(cfg => cfg.PlayerSideMargin).InclusiveBetween(0, 3);
        RuleFor(cfg => cfg.PlayerOverlap).InclusiveBetween(0, 10);
        RuleFor(cfg => cfg.RejoinSeconds).InclusiveBetween(0, 600);
        RuleForEach(cfg => cfg.BotSlots).GreaterThanOrEqualTo(0);
        RuleForEach(cfg => cfg.Drivers).ChildRules(d =>
        {
            d.RuleFor(x => x.Slot).GreaterThanOrEqualTo(0);
            d.RuleFor(x => x.Strength).InclusiveBetween(50, 110).When(x => x.Strength.HasValue);
            d.RuleFor(x => x.Aggression).InclusiveBetween(0, 100).When(x => x.Aggression.HasValue);
        });
    }
}
