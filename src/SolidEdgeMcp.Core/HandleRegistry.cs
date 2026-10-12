// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: AGPL-3.0-only

using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace SolidEdgeMcp.Core;

/// <summary>
/// Composite value (e.g. occurrence + face) that the registry identifies by value equality rather than by reference,
/// so the same combination keeps its handle even when a new object is created on each query.
/// </summary>
public interface ICompositeHandleValue;

/// <summary>
/// Opaque identifiers (<c>type:N</c>) for COM objects that the client reuses across calls.
/// The same object (same reference) keeps its id. Handles are grouped by scope (the
/// document) so they expire when it is closed, and the least used ones are evicted when the limit is reached.
/// </summary>
public sealed partial class HandleRegistry
{
    private const int MaxRemembered = 2000;

    private readonly object _gate = new();
    private readonly int _capacity;
    private readonly Dictionary<string, Entry> _byId = new(StringComparer.Ordinal);
    private readonly Dictionary<object, string> _byObject = new(IdentityComparer.Instance);
    private readonly LinkedList<string> _lru = new();
    private readonly Dictionary<string, string> _removedReasons = new(StringComparer.Ordinal);
    private readonly Queue<string> _removedOrder = new();
    private long _next;

    public HandleRegistry(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _capacity = capacity;
    }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _byId.Count;
            }
        }
    }

    /// <summary>Registers the object (or returns its id if it was already registered) with the given type and scope.</summary>
    public string Register(object value, string kind, string? scope = null)
    {
        ArgumentNullException.ThrowIfNull(value);
        string safeKind = NormalizeKind(kind);
        lock (_gate)
        {
            if (_byObject.TryGetValue(value, out string? existing))
            {
                Touch(_byId[existing]);
                return existing;
            }

            string id = $"{safeKind}:{++_next}";
            var entry = new Entry(id, value, scope);
            entry.Node = _lru.AddLast(id);
            _byId.Add(id, entry);
            _byObject.Add(value, id);

            while (_byId.Count > _capacity)
            {
                RemoveLocked(_lru.First!.Value, "it was evicted because too many handles were alive");
            }

            return id;
        }
    }

    /// <summary>Returns the object for the handle, or throws an error explaining whether it expired.</summary>
    public object Resolve(string? id, string argumentName = "handle")
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            throw ToolException.InvalidArgument($"'{argumentName}' must be a handle such as 'doc:1'.");
        }

        lock (_gate)
        {
            if (_byId.TryGetValue(id, out Entry? entry))
            {
                Touch(entry);
                return entry.Value;
            }

            if (_removedReasons.TryGetValue(id, out string? reason))
            {
                throw new ToolException("handle_expired", $"Handle '{id}' is no longer valid: {reason}.",
                    "Query the object again to get a new handle.");
            }
        }

        throw new ToolException("handle_not_found", $"Unknown handle '{id}' in '{argumentName}'.",
            "Use a handle returned by a previous call of this server session.");
    }

    public T Resolve<T>(string? id, string argumentName = "handle") where T : class
    {
        object value = Resolve(id, argumentName);
        return value as T ?? throw new ToolException(
            "handle_wrong_type",
            $"Handle '{id}' in '{argumentName}' does not refer to the expected kind of object.",
            "Check which tool returned the handle and pass one of the right kind.");
    }

    public string? GetScope(string id)
    {
        lock (_gate)
        {
            return _byId.TryGetValue(id, out Entry? entry) ? entry.Scope : null;
        }
    }

    public bool TryGetId(object value, out string? id)
    {
        lock (_gate)
        {
            return _byObject.TryGetValue(value, out id);
        }
    }

    /// <summary>Live handles of a type (e.g. "doc"), with their object.</summary>
    public IReadOnlyList<(string Id, object Value)> OfKind(string kind)
    {
        string safeKind = NormalizeKind(kind);
        lock (_gate)
        {
            return _byId.Values.Where(e => KindOf(e.Id) == safeKind).Select(e => (e.Id, e.Value)).ToList();
        }
    }

    /// <summary>
    /// Expires the handles of a scope (e.g. when a document is closed or recalculated); with
    /// <paramref name="kinds"/>, only the handles of those types (e.g. faces and edges after the model changes).
    /// </summary>
    public int InvalidateScope(string scope, string reason, params string[] kinds)
    {
        lock (_gate)
        {
            string[] ids = _byId.Values
                .Where(e => e.Scope == scope && (kinds.Length == 0 || kinds.Contains(KindOf(e.Id), StringComparer.Ordinal)))
                .Select(e => e.Id)
                .ToArray();
            foreach (string id in ids)
            {
                RemoveLocked(id, reason);
            }

            return ids.Length;
        }
    }

    public bool Remove(string id, string reason = "it was released")
    {
        lock (_gate)
        {
            return RemoveLocked(id, reason);
        }
    }

    public void Clear(string reason)
    {
        lock (_gate)
        {
            foreach (string id in _byId.Keys.ToArray())
            {
                RemoveLocked(id, reason);
            }
        }
    }

    private bool RemoveLocked(string id, string reason)
    {
        if (!_byId.Remove(id, out Entry? entry))
        {
            return false;
        }

        _byObject.Remove(entry.Value);
        _lru.Remove(entry.Node!);
        _removedReasons[id] = reason;
        _removedOrder.Enqueue(id);
        while (_removedOrder.Count > MaxRemembered)
        {
            _removedReasons.Remove(_removedOrder.Dequeue());
        }

        return true;
    }

    private void Touch(Entry entry)
    {
        _lru.Remove(entry.Node!);
        _lru.AddLast(entry.Node!);
    }

    private static string KindOf(string id) => id[..id.IndexOf(':', StringComparison.Ordinal)];

    private static string NormalizeKind(string kind)
    {
        string normalized = NonKindChars().Replace(kind.Trim().ToLowerInvariant(), "_").Trim('_');
        return normalized.Length == 0 ? "obj" : normalized;
    }

    [GeneratedRegex("[^a-z0-9_]+", RegexOptions.CultureInvariant)]
    private static partial Regex NonKindChars();

    /// <summary>Reference equality for COM objects; value equality for <see cref="ICompositeHandleValue"/>.</summary>
    private sealed class IdentityComparer : IEqualityComparer<object>
    {
        public static readonly IdentityComparer Instance = new();

        public new bool Equals(object? x, object? y) =>
            x is ICompositeHandleValue ? x.Equals(y) : ReferenceEquals(x, y);

        public int GetHashCode(object obj) =>
            obj is ICompositeHandleValue ? obj.GetHashCode() : RuntimeHelpers.GetHashCode(obj);
    }

    private sealed class Entry(string id, object value, string? scope)
    {
        public string Id { get; } = id;

        public object Value { get; } = value;

        public string? Scope { get; } = scope;

        public LinkedListNode<string>? Node { get; set; }
    }
}
