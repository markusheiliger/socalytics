using System.Text.Json;

namespace SocAlytics.Platform.Domain.Analysis;

public sealed record ConditionalPredicate(string Kind, string? CapabilityId = null, string? NodeKey = null)
{
	public const string CapabilityRequestedKind = "capabilityRequested";
	public const string NodeAcceptedKind = "nodeAccepted";

	public static ConditionalPredicate CapabilityRequested(string capabilityId) => new(CapabilityRequestedKind, capabilityId, null);

	public static ConditionalPredicate NodeAccepted(string nodeKey) => new(NodeAcceptedKind, null, nodeKey);

	public bool IsValid => Kind switch
	{
		CapabilityRequestedKind => !string.IsNullOrWhiteSpace(CapabilityId) && NodeKey is null,
		NodeAcceptedKind => !string.IsNullOrWhiteSpace(NodeKey) && CapabilityId is null,
		_ => false,
	};

	public static bool TryParseJson(string? json, out ConditionalPredicate? predicate)
	{
		predicate = null;
		if (string.IsNullOrWhiteSpace(json))
		{
			return false;
		}

		try
		{
			using var document = JsonDocument.Parse(json);
			var root = document.RootElement;
			if (root.ValueKind != JsonValueKind.Object)
			{
				return false;
			}

			string? kind = null;
			string? capabilityId = null;
			string? nodeKey = null;
			foreach (var property in root.EnumerateObject())
			{
				if (property.Value.ValueKind != JsonValueKind.String)
				{
					return false;
				}

				switch (property.Name)
				{
					case "kind": kind = property.Value.GetString(); break;
					case "capabilityId": capabilityId = property.Value.GetString(); break;
					case "nodeKey": nodeKey = property.Value.GetString(); break;
					default: return false;
				}
			}

			if (kind is null)
			{
				return false;
			}

			var candidate = new ConditionalPredicate(kind, capabilityId, nodeKey);
			if (!candidate.IsValid)
			{
				return false;
			}

			predicate = candidate;
			return true;
		}
		catch (JsonException)
		{
			return false;
		}
	}
}
