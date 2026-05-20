namespace QQDatabaseExplorer.Models;

public sealed class AvaMessageReaction
{
    public string FaceId { get; init; } = string.Empty;
    public int Count { get; init; }
    public string DisplayText { get; init; } = string.Empty;
    public string? FaceAssetPath { get; init; }

    public bool HasFaceAsset => !string.IsNullOrWhiteSpace(FaceAssetPath);
}
