using VatisSync.Core;

namespace VatisSync.ViewModels;

/// <summary>One line of the Log, with how serious it is so the view can tint it.</summary>
public sealed record LogLine(string Text, LogKind Kind)
{
    public override string ToString() => Text;
}
