using System.Text.Json;
using System.Text.Json.Serialization;

namespace RemoteSquads.Core;

public sealed class DomainException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

internal static class Guard
{
    internal static void Require(bool condition, string code, string message)
    {
        if (!condition) throw new DomainException(code, message);
    }

    internal static string Text(string? value)
    {
        Require(!string.IsNullOrWhiteSpace(value), "invalid_request", "A required reference is missing.");
        return value!;
    }

    internal static T Defined<T>(T value) where T : struct, Enum
    {
        Require(Enum.IsDefined(value), "invalid_request", "An enum value is unsupported.");
        return value;
    }

    internal static DateTimeOffset Utc(DateTimeOffset value)
    {
        Require(value.Offset == TimeSpan.Zero, "invalid_request", "A UTC timestamp is required.");
        return value;
    }
}

public static class Protocol
{
    public static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
        options.Converters.Add(new StrictEnumConverter<RecipientKind>(
            (RecipientKind.Coordinator, "coordinator"), (RecipientKind.WorkMonitor, "work_monitor"), (RecipientKind.Member, "member")));
        options.Converters.Add(new StrictEnumConverter<DeliveryMode>(
            (DeliveryMode.Immediate, "immediate"), (DeliveryMode.Enqueue, "enqueue")));
        options.Converters.Add(new StrictEnumConverter<CommandStatus>(
            (CommandStatus.Accepted, "accepted"), (CommandStatus.Applied, "applied"),
            (CommandStatus.Failed, "failed"), (CommandStatus.Uncertain, "uncertain")));
        options.Converters.Add(new StrictEnumConverter<PermissionDecision>(
            (PermissionDecision.Allow, "allow"), (PermissionDecision.Deny, "deny")));
        options.Converters.Add(new StrictEnumConverter<RunState>(
            (RunState.Requested, "requested"), (RunState.Provisioning, "provisioning"), (RunState.Starting, "starting"),
            (RunState.Running, "running"), (RunState.WaitingForInput, "waiting_for_input"), (RunState.Idle, "idle"),
            (RunState.Pausing, "pausing"), (RunState.Suspended, "suspended"), (RunState.Resuming, "resuming"),
            (RunState.Stopping, "stopping"), (RunState.Finalizing, "finalizing"), (RunState.Completed, "completed"),
            (RunState.Failed, "failed"), (RunState.RecoveryRequired, "recovery_required")));
        return options;
    }

    private sealed class StrictEnumConverter<T>(params (T Value, string Wire)[] supported) : JsonConverter<T>
        where T : struct, Enum
    {
        private readonly Dictionary<string, T> fromWire = supported.ToDictionary(p => p.Wire, p => p.Value, StringComparer.Ordinal);
        private readonly Dictionary<T, string> toWire = supported.ToDictionary(p => p.Value, p => p.Wire);

        public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.String && fromWire.TryGetValue(reader.GetString()!, out var value))
                return value;
            throw new JsonException("An exact supported enum wire value is required.");
        }

        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
        {
            if (!toWire.TryGetValue(value, out var wire)) throw new JsonException("The enum value is unsupported.");
            writer.WriteStringValue(wire);
        }
    }
}
