using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodexPresence;

/// <summary>JSON-lines host for isolated, unauthenticated Discord Rich Presence.</summary>
internal sealed class DiscordBridge : IDisposable
{
    private const int MaxInputCharacters = 1024 * 1024;
    private const int MinimumTextLength = 2;
    private const int MaximumTextLength = 128;

    private static readonly DiscordSocialNative.UpdateRichPresenceCallback UpdateCallback = OnUpdateCompleted;
    private static readonly DiscordSocialNative.FreeCallback FreeCallback = OnFreeCallback;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        MaxDepth = 16,
    };

    private readonly object outputLock = new();
    private readonly ConcurrentQueue<string> inputLines = new();
    private readonly ConcurrentDictionary<nint, byte> pendingHandles = new();
    private DiscordSocialNative.ObjectHandle client;
    private bool initialized;
    private bool disposed;
    private volatile bool inputClosed;

    private DiscordBridge(ulong applicationId)
    {
        DiscordSocialNative.Discord_Client_Init(ref client);
        initialized = true;
        try
        {
            DiscordSocialNative.Discord_Client_SetApplicationId(ref client, applicationId);
        }
        catch
        {
            DiscordSocialNative.Discord_Client_Drop(ref client);
            initialized = false;
            throw;
        }
    }

    public static async Task RunAsync(string applicationId)
    {
        if (!ulong.TryParse(applicationId, out var parsedApplicationId) || parsedApplicationId == 0)
            throw new ArgumentException("Discord Application ID is invalid.", nameof(applicationId));

        using var bridge = new DiscordBridge(parsedApplicationId);
        bridge.WriteResponse(new
        {
            @event = "ready",
            sdkVersion = $"{DiscordSocialNative.Discord_Client_GetVersionMajor()}." +
                         $"{DiscordSocialNative.Discord_Client_GetVersionMinor()}." +
                         $"{DiscordSocialNative.Discord_Client_GetVersionPatch()}",
        });
        await bridge.ProcessInputAsync();
    }

    private async Task ProcessInputAsync()
    {
        _ = Task.Run(ReadInputLoop);
        while (!disposed)
        {
            while (inputLines.TryDequeue(out var line)) HandleLine(line);
            DiscordSocialNative.Discord_RunCallbacks();
            if (inputClosed && inputLines.IsEmpty && pendingHandles.IsEmpty) return;
            await Task.Delay(10);
        }
    }

    private void ReadInputLoop()
    {
        try
        {
            while (Console.In.ReadLine() is { } line) inputLines.Enqueue(line);
        }
        catch (Exception error)
        {
            WriteResponse(new
            {
                @event = "fatal",
                message = $"Social SDK bridge input failed: {error.Message}",
            });
        }
        finally
        {
            inputClosed = true;
        }
    }

    private void HandleLine(string line)
    {
        BridgeRequest? request = null;
        try
        {
            if (line.Length > MaxInputCharacters) throw new InvalidDataException("Bridge request is too large.");
            request = JsonSerializer.Deserialize<BridgeRequest>(line, JsonOptions)
                ?? throw new InvalidDataException("Bridge request is empty.");
            if (request.Id <= 0) throw new InvalidDataException("Bridge request ID is invalid.");

            if (request.Activity is null)
            {
                DiscordSocialNative.Discord_Client_ClearRichPresence(ref client);
                WriteResponse(new { @event = "ack", request.Id });
                return;
            }

            UpdateRichPresence(request.Id, request.Activity);
        }
        catch (Exception error) when (error is JsonException or InvalidDataException or ArgumentException)
        {
            WriteResponse(new
            {
                @event = "error",
                id = request?.Id ?? 0,
                message = error.Message,
                retryable = false,
            });
        }
        catch (Exception error)
        {
            WriteResponse(new
            {
                @event = "error",
                id = request?.Id ?? 0,
                message = $"Social SDK call failed: {error.Message}",
                retryable = true,
                retryAfterMs = 5000,
            });
        }
    }

    private void UpdateRichPresence(long id, ActivityPayload payload)
    {
        var name = ValidateText(payload.ActivityName, "Activity name");
        var details = ValidateText(payload.Details, "Details");
        var state = ValidateText(payload.State, "State");
        if (payload.Type != 0) throw new InvalidDataException("Only the Playing activity type is supported.");

        var activity = new DiscordSocialNative.ObjectHandle();
        DiscordSocialNative.Discord_Activity_Init(ref activity);
        try
        {
            using (var value = new DiscordSocialNative.PinnedUtf8(name))
                DiscordSocialNative.Discord_Activity_SetName(ref activity, value.Value);
            DiscordSocialNative.Discord_Activity_SetType(ref activity, payload.Type);
            SetOptionalText(ref activity, details, isDetails: true);
            SetOptionalText(ref activity, state, isDetails: false);
            SetAssets(ref activity, payload.Assets);
            SetTimestamps(ref activity, payload.Timestamps);

            var contextHandle = GCHandle.Alloc(new UpdateContext(this, id));
            var contextPointer = GCHandle.ToIntPtr(contextHandle);
            pendingHandles.TryAdd(contextPointer, 0);
            try
            {
                DiscordSocialNative.Discord_Client_UpdateRichPresence(
                    ref client,
                    ref activity,
                    UpdateCallback,
                    FreeCallback,
                    contextPointer);
            }
            catch
            {
                ReleaseContext(contextPointer, contextHandle);
                throw;
            }
        }
        finally
        {
            DiscordSocialNative.Discord_Activity_Drop(ref activity);
        }
    }

    private static void SetOptionalText(
        ref DiscordSocialNative.ObjectHandle activity,
        string value,
        bool isDetails)
    {
        using var pinned = new DiscordSocialNative.PinnedUtf8(value);
        var nativeValue = pinned.Value;
        if (isDetails)
            DiscordSocialNative.Discord_Activity_SetDetails(ref activity, ref nativeValue);
        else
            DiscordSocialNative.Discord_Activity_SetState(ref activity, ref nativeValue);
    }

    private static void SetAssets(
        ref DiscordSocialNative.ObjectHandle activity,
        ActivityAssetsPayload? payload)
    {
        if (payload is null || string.IsNullOrWhiteSpace(payload.LargeImage)) return;
        var largeImage = ValidateText(payload.LargeImage, "Large image", allowLongMinimum: true);
        var largeText = string.IsNullOrWhiteSpace(payload.LargeText)
            ? null
            : ValidateText(payload.LargeText, "Large image text");

        var assets = new DiscordSocialNative.ObjectHandle();
        DiscordSocialNative.Discord_ActivityAssets_Init(ref assets);
        try
        {
            using (var pinned = new DiscordSocialNative.PinnedUtf8(largeImage))
            {
                var nativeValue = pinned.Value;
                DiscordSocialNative.Discord_ActivityAssets_SetLargeImage(ref assets, ref nativeValue);
            }
            if (largeText is not null)
            {
                using var pinned = new DiscordSocialNative.PinnedUtf8(largeText);
                var nativeValue = pinned.Value;
                DiscordSocialNative.Discord_ActivityAssets_SetLargeText(ref assets, ref nativeValue);
            }
            DiscordSocialNative.Discord_Activity_SetAssets(ref activity, ref assets);
        }
        finally
        {
            DiscordSocialNative.Discord_ActivityAssets_Drop(ref assets);
        }
    }

    private static void SetTimestamps(
        ref DiscordSocialNative.ObjectHandle activity,
        ActivityTimestampsPayload? payload)
    {
        if (payload?.Start is not > 0) return;
        var timestamps = new DiscordSocialNative.ObjectHandle();
        DiscordSocialNative.Discord_ActivityTimestamps_Init(ref timestamps);
        try
        {
            DiscordSocialNative.Discord_ActivityTimestamps_SetStart(ref timestamps, payload.Start.Value);
            DiscordSocialNative.Discord_Activity_SetTimestamps(ref activity, ref timestamps);
        }
        finally
        {
            DiscordSocialNative.Discord_ActivityTimestamps_Drop(ref timestamps);
        }
    }

    private static string ValidateText(string? value, string field, bool allowLongMinimum = false)
    {
        var normalized = value?.Trim() ?? string.Empty;
        var minimum = allowLongMinimum ? 1 : MinimumTextLength;
        if (normalized.Length < minimum || normalized.Length > MaximumTextLength)
            throw new InvalidDataException($"{field} must contain {minimum}–{MaximumTextLength} characters.");
        return normalized;
    }

    private static void OnUpdateCompleted(nint resultPointer, nint userData)
    {
        if (userData == 0) return;
        var handle = GCHandle.FromIntPtr(userData);
        if (handle.Target is UpdateContext context)
            context.Owner.CompleteUpdate(context.Id, resultPointer);
    }

    private void CompleteUpdate(long id, nint resultPointer)
    {
        if (resultPointer == 0)
        {
            WriteResponse(new { @event = "error", id, message = "Discord returned no result.", retryable = true, retryAfterMs = 5000 });
            return;
        }

        var result = Marshal.PtrToStructure<DiscordSocialNative.ObjectHandle>(resultPointer);
        try
        {
            var type = DiscordSocialNative.Discord_ClientResult_Type(ref result);
            if (type == 0)
            {
                WriteResponse(new { @event = "ack", id });
                return;
            }

            DiscordSocialNative.Discord_ClientResult_Error(ref result, out var nativeError);
            var message = DiscordSocialNative.ReadAndFree(nativeError);
            if (string.IsNullOrWhiteSpace(message))
            {
                DiscordSocialNative.Discord_ClientResult_ToString(ref result, out var nativeResult);
                message = DiscordSocialNative.ReadAndFree(nativeResult);
            }
            message = type switch
            {
                3 => "Discord Desktop is not ready.",
                9 => "Discord Desktop is not reachable.",
                _ when string.IsNullOrWhiteSpace(message) => $"Discord SDK error {type}.",
                _ => message,
            };

            var retryAfter = DiscordSocialNative.Discord_ClientResult_RetryAfter(ref result);
            var retryable = type is 1 or 2 or 3 or 9;
            WriteResponse(new
            {
                @event = "error",
                id,
                message,
                errorCode = DiscordSocialNative.Discord_ClientResult_ErrorCode(ref result),
                retryable,
                retryAfterMs = retryAfter > 0 ? (int)Math.Min(30_000, retryAfter * 1000) : 5000,
            });
        }
        finally
        {
            DiscordSocialNative.Discord_ClientResult_Drop(ref result);
        }
    }

    private static void OnFreeCallback(nint userData)
    {
        if (userData == 0) return;
        var handle = GCHandle.FromIntPtr(userData);
        if (handle.Target is UpdateContext context)
            context.Owner.ReleaseContext(userData, handle);
    }

    private void ReleaseContext(nint pointer, GCHandle handle)
    {
        if (!pendingHandles.TryRemove(pointer, out _)) return;
        if (handle.IsAllocated) handle.Free();
    }

    private void WriteResponse(object response)
    {
        lock (outputLock)
        {
            Console.Out.WriteLine(JsonSerializer.Serialize(response, JsonOptions));
            Console.Out.Flush();
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (initialized)
        {
            try { DiscordSocialNative.Discord_Client_ClearRichPresence(ref client); } catch { }
            try { DiscordSocialNative.Discord_Client_Drop(ref client); } catch { }
            initialized = false;
        }

        foreach (var pointer in pendingHandles.Keys)
        {
            if (!pendingHandles.TryRemove(pointer, out _)) continue;
            var handle = GCHandle.FromIntPtr(pointer);
            if (handle.IsAllocated) handle.Free();
        }
    }

    private sealed record UpdateContext(DiscordBridge Owner, long Id);

    private sealed class BridgeRequest
    {
        [JsonPropertyName("id")] public long Id { get; init; }
        [JsonPropertyName("activity")] public ActivityPayload? Activity { get; init; }
    }

    private sealed class ActivityPayload
    {
        [JsonPropertyName("name")] public string? ActivityName { get; init; }
        [JsonPropertyName("type")] public int Type { get; init; }
        [JsonPropertyName("details")] public string? Details { get; init; }
        [JsonPropertyName("state")] public string? State { get; init; }
        [JsonPropertyName("assets")] public ActivityAssetsPayload? Assets { get; init; }
        [JsonPropertyName("timestamps")] public ActivityTimestampsPayload? Timestamps { get; init; }
    }

    private sealed class ActivityAssetsPayload
    {
        [JsonPropertyName("large_image")] public string? LargeImage { get; init; }
        [JsonPropertyName("large_text")] public string? LargeText { get; init; }
    }

    private sealed class ActivityTimestampsPayload
    {
        [JsonPropertyName("start")] public ulong? Start { get; init; }
    }
}
