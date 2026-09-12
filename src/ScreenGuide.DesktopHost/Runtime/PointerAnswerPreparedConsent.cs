using System.Globalization;
using ScreenGuide.AI.Core;
using ScreenGuide.Core.Ai;
using ScreenGuide.Core.Sessions;
using ScreenGuide.Vision.Abstractions;

namespace ScreenGuide.DesktopHost.Runtime;

public sealed class PointerAnswerException(string code, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public string Code { get; } = code;
}

public sealed record PointerAnswerPreparedConsent(
    Guid ConsentId,
    Guid TurnId,
    long TurnVersion,
    PointerAnchor Anchor,
    DateTimeOffset PreparedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    string ProviderId,
    string ModelId,
    string DestinationOrigin,
    string PromptId,
    string PromptVersion,
    string PromptContentHash,
    string Question,
    string OcrText,
    string SerializedContext,
    string QuestionHash,
    string OcrHash,
    string ContextHash,
    string TargetBindingHash,
    string PreviewHash,
    int OcrLineCount,
    int RegionWidth,
    int RegionHeight,
    string RegionSource)
{
    public static readonly TimeSpan MaximumReadingAge = TimeSpan.FromSeconds(120);

    public static PointerAnswerPreparedConsent Create(
        SessionTurnRecord turn,
        PointerRegionOcrResult result,
        PromptDefinition prompt,
        DateTimeOffset preparedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(turn);
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(prompt);
        if (turn.FrozenRoute is not
            {
                Status: SessionTurnRouteStatus.Ready,
                ProviderId: { Length: > 0 } providerId,
                ModelId: { Length: > 0 } modelId,
                DataDestination: { Length: > 0 } destination
            })
        {
            throw new InvalidOperationException("指针回答没有可用的冻结聊天路由。");
        }

        var question = PointerAnswerOutboundContract.RequireQuestion(turn.InputText);
        var context = PointerAnswerOutboundContract.SerializeContext(
            result.Text,
            result.LineCount,
            result.RegionWidth,
            result.RegionHeight,
            result.RegionSource);
        var destinationOrigin = PointerAnswerOutboundContract.NormalizeHttpsOrigin(destination);
        var questionHash = PointerAnswerOutboundContract.HashText(question);
        var ocrHash = PointerAnswerOutboundContract.HashText(result.Text);
        var contextHash = PointerAnswerOutboundContract.HashText(context);
        var targetBindingHash = HashTarget(result.Anchor);
        var consentId = Guid.NewGuid();
        var expiresAtUtc = preparedAtUtc + MaximumReadingAge;
        var previewHash = PointerAnswerOutboundContract.HashText(string.Join(
            "\n",
            consentId.ToString("D"),
            turn.Id.ToString("D"),
            turn.Version.ToString(CultureInfo.InvariantCulture),
            result.Anchor.AnchorId.ToString("D"),
            providerId,
            modelId,
            destinationOrigin,
            prompt.PromptId,
            prompt.Version,
            prompt.ContentSha256,
            questionHash,
            ocrHash,
            contextHash,
            targetBindingHash,
            preparedAtUtc.ToString("O", CultureInfo.InvariantCulture),
            expiresAtUtc.ToString("O", CultureInfo.InvariantCulture)));

        return new PointerAnswerPreparedConsent(
            consentId,
            turn.Id,
            turn.Version,
            result.Anchor,
            preparedAtUtc,
            expiresAtUtc,
            providerId,
            modelId,
            destinationOrigin,
            prompt.PromptId,
            prompt.Version,
            prompt.ContentSha256,
            question,
            result.Text,
            context,
            questionHash,
            ocrHash,
            contextHash,
            targetBindingHash,
            previewHash,
            result.LineCount,
            result.RegionWidth,
            result.RegionHeight,
            result.RegionSource);
    }

    public PointerAnswerEnvelope CreateEnvelope(DateTimeOffset consumedAtUtc) => new(
        SerializedContext,
        new PointerAnswerAuditMetadata(
            ConsentId,
            TurnId,
            Anchor.AnchorId,
            PreparedAtUtc,
            ExpiresAtUtc,
            consumedAtUtc,
            ProviderId,
            ModelId,
            DestinationOrigin,
            PromptId,
            PromptVersion,
            PromptContentHash,
            QuestionHash,
            OcrHash,
            ContextHash,
            TargetBindingHash,
            PreviewHash,
            Question.Length,
            OcrText.Length,
            OcrLineCount,
            RegionWidth,
            RegionHeight,
            RegionSource));

    public override string ToString() =>
        $"PointerAnswerPreparedConsent {{ ConsentId = {ConsentId}, TurnId = {TurnId}, AnchorId = {Anchor.AnchorId}, ProviderId = {ProviderId}, ModelId = {ModelId}, DestinationOrigin = {DestinationOrigin}, PromptId = {PromptId}, PromptVersion = {PromptVersion}, QuestionHash = {QuestionHash}, OcrHash = {OcrHash}, PreviewHash = {PreviewHash}, Content = [REDACTED] }}";

    private static string HashTarget(PointerAnchor anchor) =>
        PointerAnswerOutboundContract.HashText(string.Join(
            "\n",
            anchor.AnchorId.ToString("D"),
            anchor.AppRunId.ToString("D"),
            anchor.PhysicalScreenX.ToString(CultureInfo.InvariantCulture),
            anchor.PhysicalScreenY.ToString(CultureInfo.InvariantCulture),
            anchor.NormalizedX.ToString("R", CultureInfo.InvariantCulture),
            anchor.NormalizedY.ToString("R", CultureInfo.InvariantCulture),
            anchor.Window.Target.WindowHandle.ToString(CultureInfo.InvariantCulture),
            anchor.Window.Target.ProcessId.ToString(CultureInfo.InvariantCulture),
            anchor.Window.Target.ProcessStartTimeUtc.ToString("O", CultureInfo.InvariantCulture),
            anchor.Window.Target.ProcessName,
            anchor.Window.Target.WindowTitle,
            anchor.Window.Bounds.Left.ToString(CultureInfo.InvariantCulture),
            anchor.Window.Bounds.Top.ToString(CultureInfo.InvariantCulture),
            anchor.Window.Bounds.Width.ToString(CultureInfo.InvariantCulture),
            anchor.Window.Bounds.Height.ToString(CultureInfo.InvariantCulture),
            anchor.Window.DpiX.ToString(CultureInfo.InvariantCulture),
            anchor.Window.DpiY.ToString(CultureInfo.InvariantCulture),
            anchor.CapturedAtUtc.ToString("O", CultureInfo.InvariantCulture)));
}
