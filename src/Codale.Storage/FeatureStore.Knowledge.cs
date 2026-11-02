using System.Text.Json;
using Codale.Core.Features;

namespace Codale.Storage;

public sealed partial class FeatureStore
{
    private void InitializeProjectKnowledge() => Execute("""
        CREATE TABLE IF NOT EXISTS provider_sessions (
            session_id TEXT PRIMARY KEY, provider TEXT NOT NULL, provider_session_id TEXT NOT NULL,
            worktree TEXT NOT NULL, endpoint TEXT NOT NULL
        );
        CREATE TABLE IF NOT EXISTS knowledge (
            id TEXT PRIMARY KEY, session_id TEXT NOT NULL, provider TEXT NOT NULL, worktree TEXT NOT NULL,
            revision TEXT NOT NULL, text TEXT NOT NULL, refs TEXT NOT NULL
        );
        CREATE VIRTUAL TABLE IF NOT EXISTS knowledge_fts USING fts5(id UNINDEXED, tokens);
        CREATE TABLE IF NOT EXISTS source_files (
            worktree TEXT NOT NULL, path TEXT NOT NULL, hash TEXT NOT NULL, PRIMARY KEY(worktree, path)
        );
        CREATE TABLE IF NOT EXISTS source_chunks (
            id INTEGER PRIMARY KEY, worktree TEXT NOT NULL, path TEXT NOT NULL, hash TEXT NOT NULL,
            start_line INTEGER NOT NULL, text TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS ix_source_chunks ON source_chunks(worktree, path);
        CREATE VIRTUAL TABLE IF NOT EXISTS source_fts USING fts5(chunk_id UNINDEXED, tokens);
        CREATE TABLE IF NOT EXISTS retrievals (
            id TEXT PRIMARY KEY, session_id TEXT NOT NULL, elapsed_ms INTEGER NOT NULL, tokens INTEGER NOT NULL,
            refs TEXT NOT NULL, exploration_calls INTEGER NOT NULL DEFAULT 0
            , input_tokens INTEGER NOT NULL DEFAULT 0, output_tokens INTEGER NOT NULL DEFAULT 0
        );
        CREATE TABLE IF NOT EXISTS delivered_context (
            provider_session_id TEXT NOT NULL, key TEXT NOT NULL, PRIMARY KEY(provider_session_id, key)
        );
        """);

    public IReadOnlyList<string> DeliveredContext(string providerSessionId)
    {
        lock (_gate) return Query("SELECT key FROM delivered_context WHERE provider_session_id=$s", r => r.GetString(0), ("$s", providerSessionId));
    }

    public void MarkContextDelivered(string providerSessionId, IReadOnlyList<string> keys)
    {
        lock (_gate)
        {
            using var tx = _connection.BeginTransaction();
            foreach (var key in keys)
                Execute("INSERT OR IGNORE INTO delivered_context VALUES ($s,$k)", ("$s", providerSessionId), ("$k", key));
            tx.Commit();
        }
    }

    public void ClearDeliveredContext(string providerSessionId)
    {
        lock (_gate) Execute("DELETE FROM delivered_context WHERE provider_session_id=$s", ("$s", providerSessionId));
    }

    public void SetProviderSession(ProviderSession session)
    {
        lock (_gate)
        {
            Execute("""
                INSERT INTO provider_sessions VALUES ($s, $p, $id, $w, $e)
                ON CONFLICT(session_id) DO UPDATE SET provider=$p, provider_session_id=$id, worktree=$w, endpoint=$e
                """, ("$s", session.SessionId), ("$p", session.Provider), ("$id", session.ProviderSessionId),
                ("$w", session.Worktree), ("$e", session.Endpoint));
        }
    }

    public ProviderSession? GetProviderSession(string id)
    {
        lock (_gate)
        {
            return Query("SELECT session_id, provider, provider_session_id, worktree, endpoint FROM provider_sessions WHERE session_id=$id",
                r => new ProviderSession(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4)), ("$id", id)).FirstOrDefault();
        }
    }

    public void PutKnowledge(KnowledgeHit hit)
    {
        lock (_gate)
        {
            using var tx = _connection.BeginTransaction();
            Execute("INSERT OR REPLACE INTO knowledge VALUES ($id,$s,$p,$w,$r,$t,$refs)",
                ("$id", hit.Id), ("$s", hit.SessionId), ("$p", hit.Provider), ("$w", hit.Worktree),
                ("$r", hit.Revision), ("$t", hit.Text), ("$refs", JsonSerializer.Serialize(hit.References)));
            Execute("DELETE FROM knowledge_fts WHERE id=$id", ("$id", hit.Id));
            Execute("INSERT INTO knowledge_fts VALUES ($id,$t)", ("$id", hit.Id), ("$t", SearchText.ForIndex(hit.Text)));
            tx.Commit();
        }
    }

    public IReadOnlyList<KnowledgeHit> SearchKnowledge(string query, int limit = 8, string? excludeSessionId = null)
    {
        if (SearchText.ToMatchQuery(query) is not { } match) return [];
        lock (_gate)
        {
            return Query("""
                SELECT k.id,k.session_id,k.provider,k.worktree,k.revision,k.text,k.refs
                FROM knowledge_fts f JOIN knowledge k ON k.id=f.id WHERE knowledge_fts MATCH $q
                AND ($exclude IS NULL OR k.session_id <> $exclude)
                ORDER BY bm25(knowledge_fts) LIMIT $n
                """, r => new KnowledgeHit(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4),
                    r.GetString(5), JsonSerializer.Deserialize<List<SourceReference>>(r.GetString(6)) ?? []), ("$q", match), ("$n", limit), ("$exclude", excludeSessionId));
        }
    }

    public IReadOnlyDictionary<string, string> SourceHashes(string worktree)
    {
        lock (_gate)
        {
            return Query("SELECT path,hash FROM source_files WHERE worktree=$w", r => (r.GetString(0), r.GetString(1)), ("$w", worktree))
                .ToDictionary(x => x.Item1, x => x.Item2, StringComparer.OrdinalIgnoreCase);
        }
    }

    public void ReplaceSource(string worktree, string path, string? hash, IReadOnlyList<(int StartLine, string Text)> chunks)
    {
        lock (_gate)
        {
            using var tx = _connection.BeginTransaction();
            Execute("""
                DELETE FROM source_fts WHERE chunk_id IN (SELECT id FROM source_chunks WHERE worktree=$w AND path=$p);
                DELETE FROM source_chunks WHERE worktree=$w AND path=$p;
                DELETE FROM source_files WHERE worktree=$w AND path=$p;
                """, ("$w", worktree), ("$p", path));
            if (hash is not null)
            {
                Execute("INSERT INTO source_files VALUES ($w,$p,$h)", ("$w", worktree), ("$p", path), ("$h", hash));
                foreach (var (line, text) in chunks)
                {
                    var id = Insert("INSERT INTO source_chunks(worktree,path,hash,start_line,text) VALUES ($w,$p,$h,$l,$t) RETURNING id",
                        ("$w", worktree), ("$p", path), ("$h", hash), ("$l", line), ("$t", text));
                    Execute("INSERT INTO source_fts VALUES ($id,$t)", ("$id", id), ("$t", SearchText.ForIndex(path + " " + text)));
                }
            }
            tx.Commit();
        }
    }

    public IReadOnlyList<SourceChunk> SearchSource(string query, string worktree, int limit = 12)
    {
        if (SearchText.ToMatchQuery(query) is not { } match) return [];
        lock (_gate)
        {
            return Query("""
                SELECT c.worktree,c.path,c.hash,c.start_line,c.text FROM source_fts f JOIN source_chunks c ON c.id=f.chunk_id
                WHERE source_fts MATCH $q AND c.worktree=$w ORDER BY bm25(source_fts) LIMIT $n
                """, r => new SourceChunk(r.GetString(0), r.GetString(1), r.GetString(2), r.GetInt32(3), r.GetString(4)),
                ("$q", match), ("$w", worktree), ("$n", limit));
        }
    }

    public void RecordRetrieval(string id, string sessionId, RetrievalResult result)
    {
        lock (_gate)
        {
            Execute("INSERT INTO retrievals(id,session_id,elapsed_ms,tokens,refs) VALUES ($id,$s,$ms,$t,$r)",
                ("$id", id), ("$s", sessionId), ("$ms", result.ElapsedMilliseconds), ("$t", result.Tokens), ("$r", JsonSerializer.Serialize(result.Keys)));
        }
    }

    public void RecordExploration(string retrievalId)
    {
        lock (_gate) Execute("UPDATE retrievals SET exploration_calls=exploration_calls+1 WHERE id=$id", ("$id", retrievalId));
    }

    public void RecordUsage(string retrievalId, int inputTokens, int outputTokens)
    {
        lock (_gate) Execute("UPDATE retrievals SET input_tokens=input_tokens+$i,output_tokens=output_tokens+$o WHERE id=$id",
            ("$id", retrievalId), ("$i", inputTokens), ("$o", outputTokens));
    }

    public IReadOnlyList<SourceReference> SessionReferences(string sessionId)
    {
        lock (_gate)
        {
            return Query("SELECT refs FROM knowledge WHERE session_id=$s ORDER BY rowid DESC", r => r.GetString(0), ("$s", sessionId))
                .SelectMany(json => JsonSerializer.Deserialize<List<SourceReference>>(json) ?? [])
                .DistinctBy(reference => reference.Path, StringComparer.OrdinalIgnoreCase).ToList();
        }
    }

    public void RecordSessionFile(string sessionId, string relativePath)
    {
        lock (_gate) Execute("INSERT OR REPLACE INTO feature_files VALUES ($s,$p,$at)",
            ("$s", sessionId), ("$p", relativePath), ("$at", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
    }
}
