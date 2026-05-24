using System.Threading.Tasks;
using Avalonia.Controls;
using QQDatabaseExplorer.Models;

namespace QQDatabaseExplorer.Services;

public interface IClipboardService
{
    Task SetTextAsync(string text);
    Task SetMessagePayloadAsync(Control owner, MessageCopyPayload payload);
    Task SetMessageBatchPayloadAsync(Control owner, MessageBatchCopyPayload payload);
    Task SetImageAsync(Control owner, string? imagePath);
    Task SetFileAsync(Control owner, string? filePath);
}
