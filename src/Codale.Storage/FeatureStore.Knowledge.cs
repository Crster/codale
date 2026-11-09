using System.Text.Json;
using Microsoft.Data.Sqlite;
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
        CREATE TABLE IF NOT EXISTS source_index_versions (
            worktree TEXT PRIMARY KEY, chunk_version INTEGER NOT NULL
        );
        CREATE VIRTUAL TABLE IF NOT EXISTS source_fts USING fts5(chunk_id UNINDEXED, tokens);
        CREATE TABLE IF NOT EXISTS chunk_vectors (
            chunk_id INTEGER NOT NULL, model TEXT NOT NULL, vector BLOB NOT NULL, PRIMARY KEY(chunk_id, model)
        ) WITHOUT ROWID;
        CREATE TABLE IF NOT EXISTS retrievals (
            id TEXT PRIMARY KEY, session_id TEXT NOT NULL, elapsed_ms INTEGER NOT NULL, tokens INTEGER NOT NULL,
            refs TEXT NOT NULL, exploration_calls INTEGER NOT NULL DEFAULT 0
            , input_tokens INTEGER NOT NULL DEFAULT 0, output_tokens INTEGER NOT NULL DEFAULT 0
        );
        CREATE TABLE IF NOT EXISTS delivered_context (
            provider_session_id TEXT NOT NULL, key TEXT NOT NULL, PRIMARY KEY(provider_session_id, key)
        );
        CREATE TABLE IF NOT EXISTS learned_edits (
            id INTEGER PRIMARY KEY, session_id TEXT NOT NULL, worktree TEXT NOT NULL,
            path TEXT NOT NULL, prompt TEXT NOT NULL,
            UNIQUE(session_id, path, prompt)
        );
        CREATE VIRTUAL TABLE IF NOT EXISTS learned_edits_fts USING fts5(edit_id UNINDEXED, tokens);
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

    public IReadOnlyList<KnowledgeHit> SearchKnowledge(string query, int limit = 8, string? excludeSessionId = null) =>
        SearchKnowledgeMatch(SearchText.ToMatchQuery(query), limit, excludeSessionId);

    /// <summary>Findings for project retrieval: question filler dropped and stems prefix-matched, as for source.</summary>
    public IReadOnlyList<KnowledgeHit> SearchKnowledgeTerms(string query, int limit = 3, string? excludeSessionId = null) =>
        SearchKnowledgeMatch(SearchText.ToRetrievalQuery(query), limit, excludeSessionId);

    private IReadOnlyList<KnowledgeHit> SearchKnowledgeMatch(string? match, int limit, string? excludeSessionId)
    {
        if (match is null) return [];
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

    public int SourceChunkVersion(string worktree)
    {
        lock (_gate)
            return Query("SELECT chunk_version FROM source_index_versions WHERE worktree=$w", r => r.GetInt32(0), ("$w", worktree)).FirstOrDefault();
    }

    public void SetSourceChunkVersion(string worktree, int version)
    {
        lock (_gate)
            Execute("INSERT INTO source_index_versions(worktree,chunk_version) VALUES ($w,$v) ON CONFLICT(worktree) DO UPDATE SET chunk_version=$v",
                ("$w", worktree), ("$v", version));
    }

    public void ReplaceSource(string worktree, string path, string? hash, IReadOnlyList<(int StartLine, string Text)> chunks)
    {
        lock (_gate)
        {
            using var tx = _connection.BeginTransaction();
            Execute("""
                DELETE FROM source_fts WHERE chunk_id IN (SELECT id FROM source_chunks WHERE worktree=$w AND path=$p);
                DELETE FROM chunk_vectors WHERE chunk_id IN (SELECT id FROM source_chunks WHERE worktree=$w AND path=$p);
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

    public IReadOnlyList<SourceChunk> SearchSource(string query, string worktree, int limit = 12) =>
        SearchSourceMatch(SearchText.ToMatchQuery(query), worktree, limit);

    /// <summary>Lexical leg of hybrid retrieval: filler-free, stemmed prefix terms ranked by BM25.</summary>
    public IReadOnlyList<SourceChunk> SearchSourceTerms(string query, string worktree, int limit = 40) =>
        SearchSourceMatch(SearchText.ToRetrievalQuery(query), worktree, limit);

    private IReadOnlyList<SourceChunk> SearchSourceMatch(string? match, string worktree, int limit)
    {
        if (match is null) return [];
        lock (_gate)
        {
            return Query("""
                SELECT c.worktree,c.path,c.hash,c.start_line,c.text,c.id FROM source_fts f JOIN source_chunks c ON c.id=f.chunk_id
                WHERE source_fts MATCH $q AND c.worktree=$w ORDER BY bm25(source_fts) LIMIT $n
                """, ReadChunk, ("$q", match), ("$w", worktree), ("$n", limit));
        }
    }

    private static SourceChunk ReadChunk(SqliteDataReader r) =>
        new(r.GetString(0), r.GetString(1), r.GetString(2), r.GetInt32(3), r.GetString(4), r.GetInt64(5));

    public IReadOnlyList<SourceChunk> SourceChunks(IReadOnlyCollection<long> ids)
    {
        if (ids.Count == 0) return [];
        lock (_gate)
        {
            return Query($"SELECT worktree,path,hash,start_line,text,id FROM source_chunks WHERE id IN ({string.Join(',', ids)})", ReadChunk);
        }
    }

    /// <summary>Chunks of a worktree with no vector from <paramref name="model"/> yet, for background embedding.</summary>
    public IReadOnlyList<(long Id, string Path, string Text)> ChunksMissingVectors(string worktree, string model, int limit)
    {
        lock (_gate)
        {
            return Query("""
                SELECT c.id,c.path,c.text FROM source_chunks c LEFT JOIN chunk_vectors v ON v.chunk_id=c.id AND v.model=$m
                WHERE c.worktree=$w AND v.chunk_id IS NULL LIMIT $n
                """, r => (r.GetInt64(0), r.GetString(1), r.GetString(2)), ("$w", worktree), ("$m", model), ("$n", limit));
        }
    }

    /// <summary>Stores unit vectors as int8 (see <see cref="VectorMath.Quantize"/>).</summary>
    public void PutVectors(string model, IReadOnlyList<(long ChunkId, float[] Vector)> vectors)
    {
        lock (_gate)
        {
            using var tx = _connection.BeginTransaction();
            foreach (var (id, vector) in vectors)
                Execute("INSERT OR REPLACE INTO chunk_vectors VALUES ($id,$m,$v)", ("$id", id), ("$m", model), ("$v", VectorMath.Quantize(vector)));
            tx.Commit();
        }
    }

    public QuantizedMatrix SourceVectors(string worktree, string model)
    {
        lock (_gate)
        {
            return QuantizedMatrix.From(Query("""
                SELECT v.chunk_id,v.vector FROM chunk_vectors v JOIN source_chunks c ON c.id=v.chunk_id
                WHERE c.worktree=$w AND v.model=$m
                """, r => (r.GetInt64(0), (byte[])r.GetValue(1)), ("$w", worktree), ("$m", model)));
        }
    }

    /// <summary>Drops vectors of models no longer in use, so a model upgrade does not leave the old index behind.</summary>
    public void DeleteVectorsExcept(IReadOnlyCollection<string> models)
    {
        lock (_gate)
        {
            var keep = string.Join(",", models.Select((_, i) => "$m" + i));
            Execute($"DELETE FROM chunk_vectors WHERE model NOT IN ({keep})", models.Select((m, i) => ("$m" + i, (object?)m)).ToArray());
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

    /// <summary>Associate an actual agent edit with the user request active during that turn.</summary>
    public void RecordLearnedEdit(string sessionId, string worktree, string path, string prompt)
    {
        if (SearchText.RetrievalTerms(prompt).Count == 0) return;
        path = path.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        lock (_gate)
        {
            using var tx = _connection.BeginTransaction();
            var id = Insert("INSERT OR IGNORE INTO learned_edits(session_id,worktree,path,prompt) VALUES ($s,$w,$p,$q) RETURNING id",
                ("$s", sessionId), ("$w", worktree), ("$p", path), ("$q", prompt.Length > 500 ? prompt[..500] : prompt));
            if (id > 0) Execute("INSERT INTO learned_edits_fts VALUES ($id,$t)", ("$id", id), ("$t", SearchText.ForIndex(prompt)));
            tx.Commit();
        }
    }

    public IReadOnlyList<(string Path, string Prompt)> SearchLearnedEdits(string worktree, string prompt)
    {
        if (SearchText.ToRetrievalQuery(prompt) is not { } match) return [];
        lock (_gate) return Query("""
            SELECT e.path,e.prompt FROM learned_edits_fts f JOIN learned_edits e ON e.id=f.edit_id
            WHERE learned_edits_fts MATCH $q AND e.worktree=$w ORDER BY bm25(learned_edits_fts) LIMIT 80
            """, r => (r.GetString(0), r.GetString(1)), ("$q", match), ("$w", worktree));
    }

    public IReadOnlyList<SourceChunk> SourceChunksForPath(string worktree, string path, int limit = 2)
    {
        lock (_gate) return Query("SELECT worktree,path,hash,start_line,text,id FROM source_chunks WHERE worktree=$w AND path=$p ORDER BY start_line LIMIT $n",
            ReadChunk, ("$w", worktree), ("$p", path), ("$n", limit));
    }
}
