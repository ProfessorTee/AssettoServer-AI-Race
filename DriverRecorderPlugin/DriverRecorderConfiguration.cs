using AssettoServer.Server.Configuration;
using FluentValidation;
using JetBrains.Annotations;
using YamlDotNet.Serialization;

namespace DriverRecorderPlugin;

[UsedImplicitly(ImplicitUseKindFlags.Assign, ImplicitUseTargetFlags.WithMembers)]
public class DriverRecorderConfiguration : IValidateConfiguration<DriverRecorderConfigurationValidator>
{
    [YamlMember(Description = "Samples per second the client script sends while recording (5-50)")]
    public int SampleHz { get; set; } = 20;

    [YamlMember(Description = "Folder for the recordings (relative to the server folder). The Race AI reads the driver profiles from here")]
    public string RecordingsFolder { get; set; } = "recordings";

    [YamlMember(Description = "Tell every player after joining that the server can record their driving (/rec on)")]
    public bool JoinHint { get; set; } = true;

    [YamlMember(Description = "Laps kept per player, track and car (the oldest are deleted)")]
    public int MaxLapsPerCar { get; set; } = 40;

    [YamlMember(Description = "Chat language: de or en")]
    public string Language { get; set; } = "de";
}

public class DriverRecorderConfigurationValidator : AbstractValidator<DriverRecorderConfiguration>
{
    public DriverRecorderConfigurationValidator()
    {
        RuleFor(c => c.SampleHz).InclusiveBetween(5, 50);
        RuleFor(c => c.MaxLapsPerCar).InclusiveBetween(1, 1000);
        RuleFor(c => c.RecordingsFolder).NotEmpty();
        RuleFor(c => c.Language).Must(l => l is "de" or "en");
    }
}
