using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;

public sealed class HubStore
{
    private const long CouponValidityMilliseconds = 7L * 24L * 60L * 60L * 1000L;
    private readonly string connectionString;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public HubStore(IWebHostEnvironment environment)
    {
        string dataDirectory = Path.Combine(environment.ContentRootPath, "Data");
        Directory.CreateDirectory(dataDirectory);
        connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(dataDirectory, "interaction-hub.db"),
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString();
        Initialize();
    }

    private SqliteConnection Open()
    {
        SqliteConnection connection = new(connectionString);
        connection.Open();
        using SqliteCommand pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;";
        pragma.ExecuteNonQuery();
        return connection;
    }

    private void Initialize()
    {
        using SqliteConnection db = Open();
        using SqliteCommand command = db.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS visitors(
              id TEXT PRIMARY KEY, token TEXT NOT NULL UNIQUE, alias TEXT NOT NULL,
              public_alias INTEGER NOT NULL, contributions INTEGER NOT NULL DEFAULT 0,
              created_at INTEGER NOT NULL, last_seen INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS decisions(
              id TEXT PRIMARY KEY, author_id TEXT NOT NULL, author_name TEXT NOT NULL,
              question TEXT NOT NULL, options_json TEXT NOT NULL, status TEXT NOT NULL,
              opened_at INTEGER NOT NULL, closes_at INTEGER NOT NULL, winner_id TEXT NOT NULL DEFAULT '');
            CREATE TABLE IF NOT EXISTS votes(
              decision_id TEXT NOT NULL, visitor_id TEXT NOT NULL, option_id TEXT NOT NULL,
              created_at INTEGER NOT NULL, PRIMARY KEY(decision_id, visitor_id));
            CREATE TABLE IF NOT EXISTS visitor_character(
              visitor_id TEXT NOT NULL, agent_id TEXT NOT NULL, familiarity INTEGER NOT NULL DEFAULT 0,
              last_interaction INTEGER NOT NULL, PRIMARY KEY(visitor_id, agent_id));
            CREATE TABLE IF NOT EXISTS events(
              cursor INTEGER PRIMARY KEY AUTOINCREMENT, event_id TEXT NOT NULL UNIQUE,
              kind TEXT NOT NULL, title TEXT NOT NULL, detail TEXT NOT NULL,
              agent_id TEXT NOT NULL DEFAULT '', visitor_name TEXT NOT NULL DEFAULT '', created_at INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS rewards(
              id TEXT PRIMARY KEY, visitor_id TEXT NOT NULL, campaign TEXT NOT NULL,
              code TEXT NOT NULL UNIQUE, display_name TEXT NOT NULL, issued_at INTEGER NOT NULL,
              UNIQUE(visitor_id, campaign));
            CREATE TABLE IF NOT EXISTS appreciation(
              id TEXT PRIMARY KEY, sender_id TEXT NOT NULL, recipient_id TEXT NOT NULL,
              category TEXT NOT NULL, courier_agent_id TEXT NOT NULL,
              public_consent INTEGER NOT NULL, created_at INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS newspapers(
              date TEXT PRIMARY KEY, content_json TEXT NOT NULL, created_at INTEGER NOT NULL);
            """;
        command.ExecuteNonQuery();
        EnsureColumn(db, "rewards", "expires_at", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(db, "rewards", "claimed_at", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(db, "rewards", "description", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(db, "rewards", "source_key", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(db, "rewards", "used_at", "INTEGER NOT NULL DEFAULT 0");
        using SqliteCommand migrateRewards = db.CreateCommand();
        migrateRewards.CommandText = """
            UPDATE rewards
            SET expires_at=claimed_at+$validity
            WHERE visitor_id<>'' AND claimed_at>0
              AND expires_at<claimed_at+$validity;
            UPDATE rewards
            SET claimed_at=issued_at,expires_at=issued_at+$validity
            WHERE visitor_id<>'' AND claimed_at=0 AND expires_at=0;
            """;
        migrateRewards.Parameters.AddWithValue("$validity", CouponValidityMilliseconds);
        migrateRewards.ExecuteNonQuery();
        using SqliteCommand index = db.CreateCommand();
        index.CommandText = "CREATE UNIQUE INDEX IF NOT EXISTS rewards_source_key ON rewards(source_key) WHERE source_key<>''";
        index.ExecuteNonQuery();
    }

    private static void EnsureColumn(SqliteConnection db, string table,
        string column, string definition)
    {
        using SqliteCommand inspect = db.CreateCommand();
        inspect.CommandText = "PRAGMA table_info(" + table + ")";
        using SqliteDataReader reader = inspect.ExecuteReader();
        while (reader.Read())
            if (string.Equals(reader.GetString(1), column,
                    StringComparison.OrdinalIgnoreCase))
                return;
        reader.Close();
        using SqliteCommand alter = db.CreateCommand();
        alter.CommandText = "ALTER TABLE " + table + " ADD COLUMN "
            + column + " " + definition;
        alter.ExecuteNonQuery();
    }

    public VisitorSession Register(string? token, RegisterVisitorRequest request)
    {
        long now = Now();
        string alias = CleanAlias(request.Alias);
        using SqliteConnection db = Open();
        if (!string.IsNullOrWhiteSpace(token))
        {
            using SqliteCommand update = db.CreateCommand();
            update.CommandText = "UPDATE visitors SET alias=$alias, public_alias=$public, last_seen=$now WHERE token=$token";
            update.Parameters.AddWithValue("$alias", alias);
            update.Parameters.AddWithValue("$public", request.PublicAliasConsent ? 1 : 0);
            update.Parameters.AddWithValue("$now", now);
            update.Parameters.AddWithValue("$token", token);
            if (update.ExecuteNonQuery() > 0)
                return GetVisitorByToken(db, token)!;
        }

        // Named aliases act as lightweight prototype identities across QR browser contexts.
        if (alias != "Visitor")
        {
            string? existingToken = FindCanonicalVisitorTokenByAlias(db, alias);
            if (!string.IsNullOrWhiteSpace(existingToken))
            {
                using SqliteCommand resume = db.CreateCommand();
                resume.CommandText = "UPDATE visitors SET public_alias=$public,last_seen=$now WHERE token=$token";
                resume.Parameters.AddWithValue("$public", request.PublicAliasConsent ? 1 : 0);
                resume.Parameters.AddWithValue("$now", now);
                resume.Parameters.AddWithValue("$token", existingToken);
                resume.ExecuteNonQuery();
                return GetVisitorByToken(db, existingToken)!;
            }
        }

        string id = Guid.NewGuid().ToString("N");
        string newToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        if (alias == "Visitor")
            alias = "Visitor " + id[..4].ToUpperInvariant();
        using SqliteCommand insert = db.CreateCommand();
        insert.CommandText = "INSERT INTO visitors(id,token,alias,public_alias,created_at,last_seen) VALUES($id,$token,$alias,$public,$now,$now)";
        insert.Parameters.AddWithValue("$id", id);
        insert.Parameters.AddWithValue("$token", newToken);
        insert.Parameters.AddWithValue("$alias", alias);
        insert.Parameters.AddWithValue("$public", request.PublicAliasConsent ? 1 : 0);
        insert.Parameters.AddWithValue("$now", now);
        insert.ExecuteNonQuery();
        return GetVisitorByToken(db, newToken)!;
    }

    public VisitorSession? GetVisitor(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        using SqliteConnection db = Open();
        return GetVisitorByToken(db, token);
    }

    private static VisitorSession? GetVisitorByToken(SqliteConnection db, string token)
    {
        using SqliteCommand command = db.CreateCommand();
        command.CommandText = """
            SELECT candidate.id,candidate.token,candidate.alias,candidate.public_alias,
                   candidate.contributions
            FROM visitors requested
            JOIN visitors candidate ON lower(candidate.alias)=lower(requested.alias)
            WHERE requested.token=$token
            ORDER BY candidate.contributions DESC,
                     (SELECT COUNT(*) FROM rewards WHERE visitor_id=candidate.id) DESC,
                     candidate.created_at ASC
            LIMIT 1
            """;
        command.Parameters.AddWithValue("$token", token);
        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        bool publicAlias = reader.GetInt32(3) != 0;
        string alias = reader.GetString(2);
        return new VisitorSession(reader.GetString(0), reader.GetString(1),
            publicAlias ? alias : "A returning visitor", publicAlias, reader.GetInt32(4));
    }

    private static string? FindCanonicalVisitorTokenByAlias(SqliteConnection db,
        string alias)
    {
        using SqliteCommand command = db.CreateCommand();
        command.CommandText = """
            SELECT token FROM visitors
            WHERE lower(alias)=lower($alias)
            ORDER BY contributions DESC,
                     (SELECT COUNT(*) FROM rewards WHERE visitor_id=visitors.id) DESC,
                     created_at ASC
            LIMIT 1
            """;
        command.Parameters.AddWithValue("$alias", alias);
        return command.ExecuteScalar() as string;
    }

    public DecisionView? CreateDecision(DecisionRequest request)
    {
        ResolveExpiredDecision();
        if (GetActiveDecision() != null) return null;
        long opened = Now();
        long closes = opened + Math.Clamp(request.DurationSeconds, 60, 600) * 1000L;
        using SqliteConnection db = Open();
        using SqliteCommand command = db.CreateCommand();
        command.CommandText = "INSERT INTO decisions(id,author_id,author_name,question,options_json,status,opened_at,closes_at) VALUES($id,$author,$name,$question,$options,'active',$opened,$closes)";
        command.Parameters.AddWithValue("$id", request.DecisionId);
        command.Parameters.AddWithValue("$author", request.AuthorAgentId);
        command.Parameters.AddWithValue("$name", request.AuthorDisplayName);
        command.Parameters.AddWithValue("$question", request.Question);
        command.Parameters.AddWithValue("$options", JsonSerializer.Serialize(request.Options, JsonOptions));
        command.Parameters.AddWithValue("$opened", opened);
        command.Parameters.AddWithValue("$closes", closes);
        try { command.ExecuteNonQuery(); }
        catch (SqliteException) { return GetDecision(request.DecisionId); }
        AddEvent(db, "decision-opened-" + request.DecisionId, "decision_opened",
            request.AuthorDisplayName + " asks", request.Question, request.AuthorAgentId, "", opened);
        return GetDecision(request.DecisionId);
    }

    public DecisionView? GetActiveDecision()
    {
        ResolveExpiredDecision();
        using SqliteConnection db = Open();
        using SqliteCommand command = db.CreateCommand();
        command.CommandText = "SELECT id FROM decisions WHERE status='active' ORDER BY opened_at DESC LIMIT 1";
        string? id = command.ExecuteScalar() as string;
        return id == null ? null : GetDecision(id);
    }

    public DecisionView? CastVote(string token, string decisionId, VoteRequest request)
    {
        ResolveExpiredDecision();
        using SqliteConnection db = Open();
        VisitorSession? visitor = GetVisitorByToken(db, token);
        DecisionView? decision = GetDecision(decisionId);
        if (visitor == null || decision == null || decision.Status != "active"
            || !decision.Options.Any(option => option.OptionId == request.OptionId)) return null;
        long now = Now();
        using SqliteCommand command = db.CreateCommand();
        command.CommandText = "INSERT OR IGNORE INTO votes(decision_id,visitor_id,option_id,created_at) VALUES($decision,$visitor,$option,$now)";
        command.Parameters.AddWithValue("$decision", decisionId);
        command.Parameters.AddWithValue("$visitor", visitor.VisitorId);
        command.Parameters.AddWithValue("$option", request.OptionId);
        command.Parameters.AddWithValue("$now", now);
        if (command.ExecuteNonQuery() > 0)
        {
            using SqliteCommand familiarity = db.CreateCommand();
            familiarity.CommandText = "INSERT INTO visitor_character(visitor_id,agent_id,familiarity,last_interaction) VALUES($visitor,$agent,1,$now) ON CONFLICT(visitor_id,agent_id) DO UPDATE SET familiarity=familiarity+1,last_interaction=$now";
            familiarity.Parameters.AddWithValue("$visitor", visitor.VisitorId);
            familiarity.Parameters.AddWithValue("$agent", decision.AuthorAgentId);
            familiarity.Parameters.AddWithValue("$now", now);
            familiarity.ExecuteNonQuery();
            DecisionOptionView option = decision.Options.First(value => value.OptionId == request.OptionId);
            AddEvent(db, "vote-" + decisionId + "-" + visitor.VisitorId, "vote_cast",
                decision.AuthorDisplayName + " received an answer", option.Reaction,
                decision.AuthorAgentId, visitor.DisplayName, now);
        }
        return GetDecision(decisionId);
    }

    public DecisionView? GetDecision(string id)
    {
        using SqliteConnection db = Open();
        using SqliteCommand command = db.CreateCommand();
        command.CommandText = "SELECT author_id,author_name,question,options_json,status,closes_at,winner_id FROM decisions WHERE id=$id";
        command.Parameters.AddWithValue("$id", id);
        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        DecisionOptionRequest[] options = JsonSerializer.Deserialize<DecisionOptionRequest[]>(reader.GetString(3), JsonOptions) ?? [];
        Dictionary<string, int> votes = GetVoteCounts(db, id);
        return new DecisionView
        {
            DecisionId = id, AuthorAgentId = reader.GetString(0), AuthorDisplayName = reader.GetString(1),
            Question = reader.GetString(2), Status = reader.GetString(4), ClosesAtUnixMilliseconds = reader.GetInt64(5),
            WinningOptionId = reader.GetString(6), Options = options.Select(option => new DecisionOptionView
            {
                OptionId = option.OptionId, Label = option.Label, Reaction = option.Reaction,
                Consequence = option.Consequence, Votes = votes.GetValueOrDefault(option.OptionId)
            }).ToArray()
        };
    }

    public string GetVisitorVote(string decisionId, string visitorId)
    {
        using SqliteConnection db = Open();
        using SqliteCommand command = db.CreateCommand();
        command.CommandText = "SELECT option_id FROM votes WHERE decision_id=$decision AND visitor_id=$visitor";
        command.Parameters.AddWithValue("$decision", decisionId);
        command.Parameters.AddWithValue("$visitor", visitorId);
        return command.ExecuteScalar() as string ?? "";
    }

    public void ResolveExpiredDecision()
    {
        using SqliteConnection db = Open();
        using SqliteCommand find = db.CreateCommand();
        find.CommandText = "SELECT id FROM decisions WHERE status='active' AND closes_at <= $now ORDER BY opened_at LIMIT 1";
        find.Parameters.AddWithValue("$now", Now());
        string? id = find.ExecuteScalar() as string;
        if (id == null) return;
        Dictionary<string, (int count, long first)> votes = GetVoteResolution(db, id);
        DecisionView? decision = GetDecision(id);
        if (decision == null) return;
        if (votes.Count == 0)
        {
            using SqliteCommand expire = db.CreateCommand();
            expire.CommandText = "UPDATE decisions SET status='expired' WHERE id=$id AND status='active'";
            expire.Parameters.AddWithValue("$id", id);
            expire.ExecuteNonQuery();
            AddEvent(db, "decision-expired-" + id, "decision_expired",
                decision.Question, "Nobody answered this time; the question was left for another day.",
                decision.AuthorAgentId, "", Now());
            return;
        }
        string winner = decision.Options
            .OrderByDescending(option => votes.GetValueOrDefault(option.OptionId).count)
            .ThenBy(option => votes.GetValueOrDefault(option.OptionId, (0, long.MaxValue)).Item2)
            .ThenBy(option => option.OptionId, StringComparer.Ordinal)
            .First().OptionId;
        using SqliteTransaction transaction = db.BeginTransaction();
        using SqliteCommand update = db.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = "UPDATE decisions SET status='resolved', winner_id=$winner WHERE id=$id AND status='active'";
        update.Parameters.AddWithValue("$winner", winner);
        update.Parameters.AddWithValue("$id", id);
        if (update.ExecuteNonQuery() == 0) { transaction.Rollback(); return; }
        using SqliteCommand contributors = db.CreateCommand();
        contributors.Transaction = transaction;
        contributors.CommandText = "UPDATE visitors SET contributions=contributions+1 WHERE id IN (SELECT visitor_id FROM votes WHERE decision_id=$id)";
        contributors.Parameters.AddWithValue("$id", id);
        contributors.ExecuteNonQuery();
        transaction.Commit();
        DecisionOptionView option = decision.Options.First(value => value.OptionId == winner);
        AddEvent(db, "decision-resolved-" + id, "decision_resolved", decision.Question,
            option.Consequence, decision.AuthorAgentId, "", Now());
        IssueEligibleRewards(db);
    }

    public bool AddUnityEvent(UnityEventRequest request)
    {
        using SqliteConnection db = Open();
        return AddEvent(db, request.EventId, request.Kind, request.Title, request.Detail,
            request.AgentIds.FirstOrDefault() ?? "", "",
            request.OccurredAtUnixMilliseconds > 0 ? request.OccurredAtUnixMilliseconds : Now());
    }

    public bool RecordQrScan(string? token, string scanId)
    {
        string cleanScanId = CleanValue(scanId, 80);
        if (string.IsNullOrWhiteSpace(cleanScanId))
            return false;

        using SqliteConnection db = Open();
        VisitorSession? visitor = GetVisitorByToken(db, token ?? "");
        bool canNameVisitor = visitor?.PublicAliasConsent == true;
        string visitorName = canNameVisitor ? visitor!.DisplayName : "";
        string detail = canNameVisitor
            ? visitor!.DisplayName + " opened the office QR link."
            : "A visitor opened the office QR link.";
        return AddEvent(db, "qr-scanned-" + cleanScanId, "qr_scanned",
            "QR scanned", detail, "", visitorName, Now());
    }

    public HubEventView[] GetEvents(long after, int limit = 100)
    {
        using SqliteConnection db = Open();
        using SqliteCommand command = db.CreateCommand();
        command.CommandText = "SELECT cursor,event_id,kind,title,detail,agent_id,visitor_name,created_at FROM events WHERE cursor>$after ORDER BY cursor LIMIT $limit";
        command.Parameters.AddWithValue("$after", Math.Max(0, after));
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 200));
        using SqliteDataReader reader = command.ExecuteReader();
        List<HubEventView> events = [];
        while (reader.Read()) events.Add(new HubEventView
        {
            Cursor = reader.GetInt64(0), EventId = reader.GetString(1), Kind = reader.GetString(2),
            Title = reader.GetString(3), Detail = reader.GetString(4), AgentId = reader.GetString(5),
            VisitorDisplayName = reader.GetString(6), CreatedAtUnixMilliseconds = reader.GetInt64(7)
        });
        return events.ToArray();
    }

    public long GetLatestCursor()
    {
        using SqliteConnection db = Open();
        using SqliteCommand command = db.CreateCommand();
        command.CommandText = "SELECT COALESCE(MAX(cursor),0) FROM events";
        return Convert.ToInt64(command.ExecuteScalar());
    }

    public NewspaperView? GetLatestNewspaper()
    {
        using SqliteConnection db = Open();
        using SqliteCommand command = db.CreateCommand();
        command.CommandText = "SELECT content_json FROM newspapers ORDER BY date DESC LIMIT 1";
        string? json = command.ExecuteScalar() as string;
        return json == null ? null : JsonSerializer.Deserialize<NewspaperView>(json, JsonOptions);
    }

    public void SaveNewspaper(NewspaperRequest request)
    {
        NewspaperView view = new()
        {
            Date = request.Date, Headline = request.Headline, Summary = request.Summary,
            Stories = request.Stories.Take(3).ToArray(), Quote = request.Quote,
            DecisionResult = request.DecisionResult, VisitorAcknowledgement = request.VisitorAcknowledgement,
            TomorrowTeaser = request.TomorrowTeaser
        };
        using SqliteConnection db = Open();
        using SqliteCommand command = db.CreateCommand();
        command.CommandText = "INSERT OR REPLACE INTO newspapers(date,content_json,created_at) VALUES($date,$json,$now)";
        command.Parameters.AddWithValue("$date", request.Date);
        command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(view, JsonOptions));
        command.Parameters.AddWithValue("$now", Now());
        command.ExecuteNonQuery();
        AddEvent(db, "newspaper-" + request.Date, "newspaper_published", request.Headline,
            request.Summary, "", "", Now());
    }

    public NewspaperView[] GetNewspaperArchive()
    {
        using SqliteConnection db = Open();
        using SqliteCommand command = db.CreateCommand();
        command.CommandText = "SELECT content_json FROM newspapers ORDER BY date DESC LIMIT 30";
        using SqliteDataReader reader = command.ExecuteReader();
        List<NewspaperView> values = [];
        while (reader.Read())
        {
            NewspaperView? value = JsonSerializer.Deserialize<NewspaperView>(reader.GetString(0), JsonOptions);
            if (value != null) values.Add(value);
        }
        return values.ToArray();
    }

    public RewardView[] GetRewards(string visitorId)
    {
        using SqliteConnection db = Open();
        using SqliteCommand command = db.CreateCommand();
        command.CommandText = "SELECT id,code,display_name,issued_at,expires_at,description,used_at FROM rewards WHERE visitor_id=$visitor ORDER BY issued_at DESC";
        command.Parameters.AddWithValue("$visitor", visitorId);
        using SqliteDataReader reader = command.ExecuteReader();
        List<RewardView> rewards = [];
        while (reader.Read()) rewards.Add(new RewardView
        {
            RewardId = reader.GetString(0), Code = reader.GetString(1), DisplayName = reader.GetString(2),
            IssuedAtUnixMilliseconds = reader.GetInt64(3),
            ExpiresAtUnixMilliseconds = reader.GetInt64(4),
            Description = reader.GetString(5), Claimed = true,
            UsedAtUnixMilliseconds = reader.GetInt64(6),
            Used = reader.GetInt64(6) > 0, PrototypeOnly = true
        });
        return rewards.ToArray();
    }

    public RewardView? GetRewardByCode(string code)
    {
        using SqliteConnection db = Open();
        using SqliteCommand command = db.CreateCommand();
        command.CommandText = "SELECT id,code,display_name,issued_at,expires_at,description,visitor_id,used_at FROM rewards WHERE code=$code";
        command.Parameters.AddWithValue("$code", code);
        using SqliteDataReader reader = command.ExecuteReader();
        return reader.Read() ? new RewardView
        {
            RewardId = reader.GetString(0), Code = reader.GetString(1),
            DisplayName = reader.GetString(2), IssuedAtUnixMilliseconds = reader.GetInt64(3),
            ExpiresAtUnixMilliseconds = reader.GetInt64(4), Description = reader.GetString(5),
            Claimed = !string.IsNullOrWhiteSpace(reader.GetString(6)),
            UsedAtUnixMilliseconds = reader.GetInt64(7),
            Used = reader.GetInt64(7) > 0, PrototypeOnly = true
        } : null;
    }

    public RewardView? GetActiveClaimableReward()
    {
        using SqliteConnection db = Open();
        using SqliteCommand command = db.CreateCommand();
        command.CommandText = "SELECT id,code,display_name,issued_at,expires_at,description FROM rewards WHERE visitor_id='' AND expires_at>$now ORDER BY issued_at DESC LIMIT 1";
        command.Parameters.AddWithValue("$now", Now());
        using SqliteDataReader reader = command.ExecuteReader();
        return reader.Read() ? ReadClaimableReward(reader) : null;
    }

    public RewardView? IssueClaimableReward(RewardIssueRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.SourceEventId)
            || string.IsNullOrWhiteSpace(request.DisplayName))
            return null;
        using SqliteConnection db = Open();
        string source = CleanValue(request.SourceEventId, 96);
        using SqliteCommand existing = db.CreateCommand();
        existing.CommandText = "SELECT id,code,display_name,issued_at,expires_at,description,visitor_id FROM rewards WHERE source_key=$source";
        existing.Parameters.AddWithValue("$source", source);
        using (SqliteDataReader reader = existing.ExecuteReader())
            if (reader.Read())
                return string.IsNullOrWhiteSpace(reader.GetString(6))
                    && reader.GetInt64(4) > Now()
                        ? ReadClaimableReward(reader) : null;

        long now = Now();
        long expires = now + Math.Clamp(request.ClaimWindowSeconds, 60, 1800) * 1000L;
        string id = Guid.NewGuid().ToString("N");
        string code = "OFFICE-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(4));
        using SqliteCommand insert = db.CreateCommand();
        insert.CommandText = "INSERT INTO rewards(id,visitor_id,campaign,code,display_name,issued_at,expires_at,claimed_at,description,source_key) VALUES($id,'',$campaign,$code,$name,$now,$expires,0,$description,$source)";
        insert.Parameters.AddWithValue("$id", id);
        insert.Parameters.AddWithValue("$campaign", "claim-" + source);
        insert.Parameters.AddWithValue("$code", code);
        insert.Parameters.AddWithValue("$name", CleanValue(request.DisplayName, 64));
        insert.Parameters.AddWithValue("$now", now);
        insert.Parameters.AddWithValue("$expires", expires);
        insert.Parameters.AddWithValue("$description", CleanValue(request.Description, 180));
        insert.Parameters.AddWithValue("$source", source);
        insert.ExecuteNonQuery();
        AddEvent(db, "reward-available-" + id, "reward_available",
            request.DisplayName, "A reward is available to claim for five minutes.", "", "", now);
        return GetRewardByCode(code);
    }

    public bool ClaimReward(string token, string code)
    {
        using SqliteConnection db = Open();
        VisitorSession? visitor = GetVisitorByToken(db, token);
        if (visitor == null) return false;
        long now = Now();
        using SqliteCommand update = db.CreateCommand();
        update.CommandText = "UPDATE rewards SET visitor_id=$visitor,claimed_at=$now,expires_at=$validUntil WHERE code=$code AND visitor_id='' AND expires_at>$now";
        update.Parameters.AddWithValue("$visitor", visitor.VisitorId);
        update.Parameters.AddWithValue("$now", now);
        update.Parameters.AddWithValue("$validUntil", now + CouponValidityMilliseconds);
        update.Parameters.AddWithValue("$code", code);
        if (update.ExecuteNonQuery() == 0) return false;
        AddEvent(db, "reward-claimed-" + code, "reward_claimed",
            "Coupon claimed", visitor.DisplayName + " claimed the office reward.",
            "", visitor.PublicAliasConsent ? visitor.DisplayName : "", now);
        return true;
    }

    public bool UseReward(string token, string code)
    {
        using SqliteConnection db = Open();
        VisitorSession? visitor = GetVisitorByToken(db, token);
        if (visitor == null) return false;
        long now = Now();
        using SqliteCommand update = db.CreateCommand();
        update.CommandText = "UPDATE rewards SET used_at=$now WHERE code=$code AND visitor_id=$visitor AND used_at=0 AND expires_at>$now";
        update.Parameters.AddWithValue("$now", now);
        update.Parameters.AddWithValue("$code", code);
        update.Parameters.AddWithValue("$visitor", visitor.VisitorId);
        if (update.ExecuteNonQuery() == 0) return false;
        AddEvent(db, "reward-used-" + code, "reward_used", "Coupon used",
            visitor.DisplayName + " used an office coupon.", "",
            visitor.PublicAliasConsent ? visitor.DisplayName : "", now);
        return true;
    }

    private static RewardView ReadClaimableReward(SqliteDataReader reader) => new()
    {
        RewardId = reader.GetString(0), Code = reader.GetString(1),
        DisplayName = reader.GetString(2), IssuedAtUnixMilliseconds = reader.GetInt64(3),
        ExpiresAtUnixMilliseconds = reader.GetInt64(4), Description = reader.GetString(5),
        Claimed = false, PrototypeOnly = true
    };

    public bool AddAppreciation(string token, AppreciationRequest request)
    {
        string[] allowed = ["help", "kindness", "teamwork", "creativity", "welcome"];
        if (!allowed.Contains(request.Category, StringComparer.OrdinalIgnoreCase)) return false;
        using SqliteConnection db = Open();
        VisitorSession? sender = GetVisitorByToken(db, token);
        if (sender == null || sender.VisitorId == request.RecipientVisitorId) return false;
        using SqliteCommand recipientCommand = db.CreateCommand();
        recipientCommand.CommandText = "SELECT alias,public_alias FROM visitors WHERE id=$id";
        recipientCommand.Parameters.AddWithValue("$id", request.RecipientVisitorId);
        using SqliteDataReader reader = recipientCommand.ExecuteReader();
        if (!reader.Read()) return false;
        bool recipientPublic = reader.GetInt32(1) != 0;
        string recipient = recipientPublic ? reader.GetString(0) : "a colleague";
        reader.Close();
        string id = Guid.NewGuid().ToString("N");
        using SqliteCommand insert = db.CreateCommand();
        insert.CommandText = "INSERT INTO appreciation(id,sender_id,recipient_id,category,courier_agent_id,public_consent,created_at) VALUES($id,$sender,$recipient,$category,$courier,$public,$now)";
        insert.Parameters.AddWithValue("$id", id);
        insert.Parameters.AddWithValue("$sender", sender.VisitorId);
        insert.Parameters.AddWithValue("$recipient", request.RecipientVisitorId);
        insert.Parameters.AddWithValue("$category", request.Category.ToLowerInvariant());
        insert.Parameters.AddWithValue("$courier", request.CourierAgentId);
        insert.Parameters.AddWithValue("$public", request.PublicConsent ? 1 : 0);
        insert.Parameters.AddWithValue("$now", Now());
        insert.ExecuteNonQuery();
        bool showPublicly = request.PublicConsent && sender.PublicAliasConsent && recipientPublic;
        AddEvent(db, "appreciation-" + id, "appreciation", "A note of appreciation",
            showPublicly ? sender.DisplayName + " appreciated " + recipient + " for " + request.Category + "." : "A private appreciation note was delivered.",
            request.CourierAgentId, showPublicly ? sender.DisplayName : "", Now());
        return true;
    }

    public (string id, string name)[] GetPublicVisitors()
    {
        using SqliteConnection db = Open();
        using SqliteCommand command = db.CreateCommand();
        command.CommandText = "SELECT id,alias FROM visitors WHERE public_alias=1 ORDER BY last_seen DESC LIMIT 30";
        using SqliteDataReader reader = command.ExecuteReader();
        List<(string, string)> values = [];
        while (reader.Read()) values.Add((reader.GetString(0), reader.GetString(1)));
        return values.ToArray();
    }

    public string NewspaperNeededDate()
    {
        DateTime now = DateTime.Now;
        string target = (now.Hour >= 18 ? now.Date : now.Date.AddDays(-1)).ToString("yyyy-MM-dd");
        using SqliteConnection db = Open();
        using SqliteCommand command = db.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM newspapers WHERE date=$date";
        command.Parameters.AddWithValue("$date", target);
        return Convert.ToInt32(command.ExecuteScalar()) == 0 ? target : "";
    }

    private void IssueEligibleRewards(SqliteConnection db)
    {
        using SqliteCommand command = db.CreateCommand();
        command.CommandText = "SELECT id FROM visitors WHERE contributions>=3 AND NOT EXISTS(SELECT 1 FROM rewards WHERE visitor_id=visitors.id AND campaign='story-contributor-v1')";
        using SqliteDataReader reader = command.ExecuteReader();
        List<string> ids = [];
        while (reader.Read()) ids.Add(reader.GetString(0));
        reader.Close();
        foreach (string visitorId in ids)
        {
            string rewardId = Guid.NewGuid().ToString("N");
            string code = "STORY-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(4));
            long now = Now();
            using SqliteCommand insert = db.CreateCommand();
            insert.CommandText = "INSERT OR IGNORE INTO rewards(id,visitor_id,campaign,code,display_name,issued_at,expires_at,claimed_at) VALUES($id,$visitor,'story-contributor-v1',$code,'Story Contributor Surprise',$now,$expires,$now)";
            insert.Parameters.AddWithValue("$id", rewardId);
            insert.Parameters.AddWithValue("$visitor", visitorId);
            insert.Parameters.AddWithValue("$code", code);
            insert.Parameters.AddWithValue("$now", now);
            insert.Parameters.AddWithValue("$expires", now + CouponValidityMilliseconds);
            if (insert.ExecuteNonQuery() > 0)
            {
                using SqliteCommand character = db.CreateCommand();
                character.CommandText = "SELECT agent_id FROM visitor_character WHERE visitor_id=$visitor ORDER BY familiarity DESC,last_interaction DESC LIMIT 1";
                character.Parameters.AddWithValue("$visitor", visitorId);
                string agentId = character.ExecuteScalar() as string ?? "";
                AddEvent(db, "reward-" + rewardId, "reward_issued", "A character prepared a surprise",
                    "A returning visitor earned a prototype Story Contributor reward.", agentId, "", Now());
            }
        }
    }

    private static Dictionary<string, int> GetVoteCounts(SqliteConnection db, string decisionId)
    {
        using SqliteCommand command = db.CreateCommand();
        command.CommandText = "SELECT option_id,COUNT(*) FROM votes WHERE decision_id=$id GROUP BY option_id";
        command.Parameters.AddWithValue("$id", decisionId);
        using SqliteDataReader reader = command.ExecuteReader();
        Dictionary<string, int> values = [];
        while (reader.Read()) values[reader.GetString(0)] = reader.GetInt32(1);
        return values;
    }

    private static Dictionary<string, (int count, long first)> GetVoteResolution(SqliteConnection db, string decisionId)
    {
        using SqliteCommand command = db.CreateCommand();
        command.CommandText = "SELECT option_id,COUNT(*),MAX(created_at) FROM votes WHERE decision_id=$id GROUP BY option_id";
        command.Parameters.AddWithValue("$id", decisionId);
        using SqliteDataReader reader = command.ExecuteReader();
        Dictionary<string, (int, long)> values = [];
        while (reader.Read()) values[reader.GetString(0)] = (reader.GetInt32(1), reader.GetInt64(2));
        return values;
    }

    private static bool AddEvent(SqliteConnection db, string eventId, string kind, string title,
        string detail, string agentId, string visitorName, long createdAt)
    {
        using SqliteCommand command = db.CreateCommand();
        command.CommandText = "INSERT OR IGNORE INTO events(event_id,kind,title,detail,agent_id,visitor_name,created_at) VALUES($id,$kind,$title,$detail,$agent,$visitor,$created)";
        command.Parameters.AddWithValue("$id", eventId);
        command.Parameters.AddWithValue("$kind", kind ?? "world");
        command.Parameters.AddWithValue("$title", title ?? "");
        command.Parameters.AddWithValue("$detail", detail ?? "");
        command.Parameters.AddWithValue("$agent", agentId ?? "");
        command.Parameters.AddWithValue("$visitor", visitorName ?? "");
        command.Parameters.AddWithValue("$created", createdAt);
        bool inserted = command.ExecuteNonQuery() > 0;
        if (inserted)
        {
            using SqliteCommand trim = db.CreateCommand();
            trim.CommandText = "DELETE FROM events WHERE cursor <= (SELECT COALESCE(MAX(cursor),0)-5000 FROM events)";
            trim.ExecuteNonQuery();
        }
        return inserted;
    }

    private static string CleanAlias(string? alias)
    {
        string value = string.Join(' ', (alias ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (value.Length > 24) value = value[..24];
        return string.IsNullOrWhiteSpace(value) ? "Visitor" : value;
    }

    private static string CleanValue(string? value, int maximumLength)
    {
        string result = string.Join(' ', (value ?? "").Split(
            (char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return result.Length > maximumLength ? result[..maximumLength] : result;
    }

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}
