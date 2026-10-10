using System.Text.RegularExpressions;

namespace SolidEdgeMcp.Core;

/// <summary>
/// Identificadores opacos (<c>tipo:N</c>) para objetos COM que el cliente reutiliza entre llamadas.
/// Un mismo objeto (misma referencia) conserva su id. Los handles se agrupan por ámbito (el
/// documento) para caducarlos al cerrarlo, y se expulsan los menos usados al llegar al límite.
/// </summary>
public sealed partial class HandleRegistry
{
    private const int MaxRemembered = 2000;

    private readonly object _gate = new();
    private readonly int _capacity;
    private readonly Dictionary<string, Entry> _byId = new(StringComparer.Ordinal);
    private readonly Dictionary<object, string> _byObject = new(ReferenceEqualityComparer.Instance);
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

    /// <summary>Registra el objeto (o devuelve su id si ya estaba) con el tipo y ámbito dados.</summary>
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

    /// <summary>Devuelve el objeto del handle o lanza un error explicando si caducó.</summary>
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

    /// <summary>
    /// Caduca los handles de un ámbito (p. ej. al cerrar o recalcular un documento); con
    /// <paramref name="kinds"/>, solo los de esos tipos (p. ej. caras y aristas tras cambiar el modelo).
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

    private sealed class Entry(string id, object value, string? scope)
    {
        public string Id { get; } = id;

        public object Value { get; } = value;

        public string? Scope { get; } = scope;

        public LinkedListNode<string>? Node { get; set; }
    }
}
