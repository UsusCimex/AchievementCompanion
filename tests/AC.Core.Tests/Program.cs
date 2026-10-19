using System.Reflection;
using System.Text;
using AC.Core;
using AC.Core.SteamFormats;

// Mini runner: every public static void method of the Tests class is a test.
int failed = 0, passed = 0;
Loc.SetLanguage(Loc.Fallback);
foreach (var m in typeof(Tests).GetMethods(BindingFlags.Public | BindingFlags.Static))
{
    try
    {
        m.Invoke(null, null);
        passed++;
        Console.WriteLine($"OK    {m.Name}");
    }
    catch (TargetInvocationException e)
    {
        failed++;
        Console.WriteLine($"FAIL  {m.Name}: {e.InnerException?.Message}");
    }
}
Console.WriteLine($"Passed: {passed}, Failed: {failed}");
return failed == 0 ? 0 : 1;

static class Assert
{
    public static void True(bool cond, string msg = "expected true")
    {
        if (!cond) throw new Exception(msg);
    }

    public static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new Exception($"expected {expected}, got {actual}");
    }
}

static class Tests
{
    static GameData SampleGame()
    {
        var g = new GameData { AppId = 1, Name = "Test", CompletionMinutes = 600, PlaytimeMinutes = 0 };
        void Add(string id, string name, string desc, double pct) =>
            g.Achievements.Add(new AchievementDef { ApiName = id, Name = name, Description = desc, GlobalPercent = pct });
        Add("ch1", "Глава 1", "Пройдите главу 1", 90);
        Add("ch5", "Finale", "Complete chapter 5", 30);
        Add("miss", "Mercy", "Spare the guard before leaving the prison", 20);
        Add("grind", "Collector", "Collect all 300 feathers", 2);
        Add("misc", "Photographer", "Take a photo", 50);
        GameLogic.AutoClassify(g);
        TimeEstimator.EstimateFromGlobalPercent(g);
        return g;
    }

    public static void Classifier_Basic()
    {
        Assert.Equal(AchievementKind.Story, KindClassifier.Classify("Глава 1", "Пройдите главу 1"));
        Assert.Equal(AchievementKind.Story, KindClassifier.Classify("Finale", "Complete chapter 5"));
        Assert.Equal(AchievementKind.Missable, KindClassifier.Classify("Mercy", "Spare the guard before leaving the prison"));
        Assert.Equal(AchievementKind.Grind, KindClassifier.Classify("Collector", "Collect all 300 feathers"));
        Assert.Equal(AchievementKind.Grind, KindClassifier.Classify("Охотник", "Убейте 1000 врагов"));
        Assert.Equal(AchievementKind.Missable, KindClassifier.Classify("Призрак", "Пройдите уровень, ни разу не попавшись"));
        Assert.Equal(AchievementKind.Other, KindClassifier.Classify("Photographer", "Take a photo"));
        Assert.Equal(AchievementKind.Unknown, KindClassifier.Classify("", ""));
    }

    public static void Classifier_RussianFormsAndPlaces()
    {
        Loc.SetLanguage("ru");
        // Steam often uses the infinitive rather than the imperative
        Assert.Equal(AchievementKind.Grind, KindClassifier.Classify("Ягодные места", "Собрать 80 ягод"));
        Assert.Equal(AchievementKind.Grind, KindClassifier.Classify("Охотник", "Убить 50 врагов"));
        Assert.Equal(AchievementKind.Grind, KindClassifier.Classify("Следопыт", "Найти все секреты"));
        // a chapter mention is a place, not story
        Assert.Equal(AchievementKind.Other, KindClassifier.Classify("Сердце", "Получить Хрустальное сердце в главе 1"));
        Assert.Equal("Глава 1", KindClassifier.Location("Сердце", "Получить Хрустальное сердце в главе 1"));
        Assert.Equal(AchievementKind.Story, KindClassifier.Classify("Сторона Б", "Завершить «Сторону Б» главы 2"));
        Assert.Equal("Глава 2", KindClassifier.Location("Сторона Б", "Завершить «Сторону Б» главы 2"));
        // a chapter number is not a grind counter
        Assert.Equal(AchievementKind.Other, KindClassifier.Classify("Тайник", "Получить ключ в главе 10"));
        Assert.Equal("Глава 10", KindClassifier.Location("Тайник", "Получить ключ в главе 10"));
        Assert.Equal(AchievementKind.Story, KindClassifier.Classify("Farewell", "Complete Chapter 9"));
        Assert.Equal("Глава 9", KindClassifier.Location("Farewell", "Complete Chapter 9"));
        Assert.Equal("Акт II", KindClassifier.Location("Hidden Path", "Find the hidden path in Act II"));
        Assert.Equal((string?)null, KindClassifier.Location("Активист", "Активируйте 3 рычага"));
        Assert.Equal((string?)null, KindClassifier.Location("Photographer", "Take a photo"));
    }

    public static void AutoGroup_RespectsManualAndLegacy()
    {
        Loc.SetLanguage("ru");
        var g = SampleGame();
        Assert.Equal("Глава 5", g.Tags["ch5"].Group); // from "Complete chapter 5"
        Assert.True(!g.Tags["ch5"].GroupIsManual, "group detected automatically");
        Assert.True(GameLogic.BuildRows(g, Percentile.P50).Single(r => r.ApiName == "ch5").GroupIsAuto, "row knows the group is auto");

        GameLogic.SetGroup(g, "ch5", ""); // the user cleared the group - auto-detection will not bring it back
        GameLogic.AutoClassify(g);
        Assert.Equal("", g.Tags["ch5"].Group);

        GameLogic.SetGroup(g, "misc", "Фоторежим");
        GameLogic.AutoClassify(g);
        Assert.Equal("Фоторежим", g.Tags["misc"].Group);

        // old format: a group without a source was set by the user
        Assert.True(new AchievementTag { Group = "Старая группа" }.GroupIsManual);
        Assert.True(!new AchievementTag().GroupIsManual);
    }

    public static void Classifier_PlacesFollowLanguage()
    {
        Loc.SetLanguage("en");
        Assert.Equal("Chapter 9", KindClassifier.Location("Farewell", "Complete Chapter 9"));
        Assert.Equal("Act II", KindClassifier.Location("Hidden Path", "Find the hidden path in Act II"));
        Assert.Equal("Episode 1", KindClassifier.Location("Сердце", "Пройти эпизод 1"));
    }

    public static void ManualKind_NotOverwrittenByAuto()
    {
        var g = SampleGame();
        GameLogic.SetKind(g, "misc", AchievementKind.Missable);
        GameLogic.AutoClassify(g);
        Assert.Equal(AchievementKind.Missable, g.Tags["misc"].Kind);
    }

    public static void Estimator_MonotonicByRarity()
    {
        var g = SampleGame();
        Assert.Equal(0.0, g.Estimates["ch1"].P50!.Value);
        Assert.Equal(600.0, g.Estimates["grind"].P50!.Value);
        Assert.True(g.Estimates["ch5"].P50 < g.Estimates["miss"].P50, "rarer must come later");
        Assert.True(g.Estimates["miss"].P90 > g.Estimates["miss"].P50, "P90 above P50");
    }

    public static void Estimator_NoCompletionTime_NoEstimates()
    {
        var g = SampleGame();
        g.CompletionMinutes = null;
        TimeEstimator.EstimateFromGlobalPercent(g);
        Assert.Equal(0, g.Estimates.Count);
    }

    public static void ManualEstimate_Wins()
    {
        var g = SampleGame();
        g.ManualEstimates["grind"] = 5;
        var row = GameLogic.BuildRows(g, Percentile.P50).Single(r => r.ApiName == "grind");
        Assert.Equal(5.0, row.EstimateMinutes);
        Assert.True(row.EstimateIsManual);
    }

    public static void Status_ManualThenSteam()
    {
        var g = SampleGame();
        var s = g.StateOf("ch5");
        s.ManualDone = true;
        s.Note = "играл под другим AppID";
        Assert.Equal(EffectiveStatus.ManualDone, GameLogic.StatusOf(s));

        var t = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
        var fresh = GameLogic.ApplySteamState(g, new[] { ("ch5", true, (DateTimeOffset?)t) }, DateTimeOffset.Now);
        Assert.Equal(1, fresh.Count);
        Assert.Equal(EffectiveStatus.Unlocked, GameLogic.StatusOf(g.States["ch5"]));
        Assert.Equal("играл под другим AppID", g.States["ch5"].Note);

        fresh = GameLogic.ApplySteamState(g, new[] { ("ch5", true, (DateTimeOffset?)t) }, DateTimeOffset.Now);
        Assert.Equal(0, fresh.Count);
    }

    public static void ManualMarks_IgnoredWhenOff()
    {
        var g = SampleGame();
        g.CompletionMinutes = null;
        g.PlaytimeMinutes = 300;
        g.StateOf("ch1").SteamUnlocked = true;
        g.StateOf("grind").ManualDone = true;
        TimeEstimator.EstimateFromGlobalPercent(g);
        Assert.Equal(300.0, g.DerivedCompletionMinutes); // the rarest is marked: the whole scale is covered

        g.CountManualMarks = false;
        Assert.Equal(EffectiveStatus.Locked, GameLogic.BuildRows(g, Percentile.P50).Single(r => r.ApiName == "grind").Status);
        Assert.Equal((1, 0, 5), GameLogic.Progress(g));
        Assert.True(Planner.Build(g, Percentile.P50).Ordered.Any(r => r.ApiName == "grind"), "without marks grind stays in the plan");
        Assert.Equal(0, new AchievementQuery { Status = StatusFilter.NotCounted }.Apply(g, Percentile.P50).Count);
        TimeEstimator.EstimateFromGlobalPercent(g);
        Assert.Equal((double?)null, g.DerivedCompletionMinutes);
        Assert.True(g.States["grind"].ManualDone, "the mark is kept, just not counted");
    }

    public static void Planner_StageOrder()
    {
        var g = SampleGame();
        var plan = Planner.Build(g, Percentile.P50);
        var order = plan.Ordered.Select(r => r.ApiName).ToList();
        Assert.Equal("miss", order[0]);
        Assert.True(order.IndexOf("ch1") < order.IndexOf("ch5"), "story ordered by time");
        Assert.Equal("grind", order[^1]);
    }

    public static void Planner_SkipsDone_AndRemaining()
    {
        var g = SampleGame();
        g.StateOf("grind").ManualDone = true;
        var plan = Planner.Build(g, Percentile.P50);
        Assert.True(plan.Ordered.All(r => r.ApiName != "grind"), "done ones are not in the plan");
        // The rarest one is done (luck or a mark), the rest is behind it and costs its stretches: up to miss.
        Assert.Equal(Math.Round(g.Estimates["miss"].P50!.Value, 1), Math.Round(plan.RemainingMinutes!.Value, 1));

        // Past ch5: what is left is the way to miss, plus misc left behind (the stretch from ch1 to it).
        g.StateOf("ch5").SteamUnlocked = true;
        plan = Planner.Build(g, Percentile.P50);
        double expected = g.Estimates["miss"].P50!.Value - g.Estimates["ch5"].P50!.Value + g.Estimates["misc"].P50!.Value;
        Assert.Equal(Math.Round(expected, 1), Math.Round(plan.RemainingMinutes!.Value, 1));
    }

    public static void Planner_IdlePlaytimeChangesNothing()
    {
        // Hours without a single unlock (AFK, a game left open) neither shorten nor stretch the estimate.
        var g = SampleGame();
        foreach (var minutes in new[] { 0, 590, 59000 })
        {
            g.PlaytimeMinutes = minutes;
            var plan = Planner.Build(g, Percentile.P50);
            Assert.Equal(1.0, plan.Pace);
            Assert.Equal(600.0, Math.Round(plan.RemainingMinutes!.Value, 1));
        }
    }

    public static void Planner_PaceFromOwnProgress()
    {
        // Slow player: 500 of the median 600 minutes played, yet only an early one unlocked (ch5 - about 29% of the scale).
        var g = SampleGame();
        g.PlaytimeMinutes = 500;
        g.StateOf("ch5").SteamUnlocked = true;
        var plan = Planner.Build(g, Percentile.P50);
        Assert.True(plan.Pace > 2.5 && plan.Pace <= Planner.MaxPace, $"pace {plan.Pace}");
        Assert.True(plan.RemainingMinutes > 600, $"remaining {plan.RemainingMinutes} with pace");

        // Fast player: the median is not sped up.
        g.PlaytimeMinutes = 60;
        plan = Planner.Build(g, Percentile.P50);
        Assert.Equal(1.0, plan.Pace);
        Assert.True(plan.RemainingMinutes <= 540.0001, $"remaining {plan.RemainingMinutes}");
    }

    public static void Planner_CompleteGame()
    {
        var g = SampleGame();
        foreach (var a in g.Achievements) g.StateOf(a.ApiName).SteamUnlocked = true;
        var plan = Planner.Build(g, Percentile.P50);
        Assert.True(plan.IsComplete, "all unlocked");
        Assert.Equal((double?)null, plan.RemainingMinutes);
    }

    public static void Planner_Groups()
    {
        var g = SampleGame();
        g.Achievements.Add(new AchievementDef { ApiName = "nd", Name = "No death", Description = "Finish without dying", GlobalPercent = 1 });
        GameLogic.AutoClassify(g);
        g.TagOf("miss").Group = "Финальный забег";
        g.TagOf("nd").Group = "финальный забег";
        var plan = Planner.Build(g, Percentile.P50);
        var section = plan.Sections.Single(s => s.Stage == PlanStage.Missable);
        Assert.Equal(1, section.Groups.Count);
        Assert.Equal(2, section.Groups[0].Items.Count);
    }

    public static void Query_FiltersAndSort()
    {
        var g = SampleGame();
        g.StateOf("ch1").SteamUnlocked = true;
        g.StateOf("misc").ManualDone = true;
        var q = new AchievementQuery { Status = StatusFilter.Open, Sort = SortMode.Rarity };
        var rows = q.Apply(g, Percentile.P50);
        Assert.True(rows.All(r => r.ApiName != "ch1" && r.ApiName != "misc"));
        Assert.Equal("grind", rows[0].ApiName);

        q = new AchievementQuery { Status = StatusFilter.Done, Sort = SortMode.Name };
        Assert.Equal("ch1,misc", string.Join(",", q.Apply(g, Percentile.P50).Select(r => r.ApiName).OrderBy(n => n)));
        q = new AchievementQuery { Status = StatusFilter.NotCounted };
        Assert.Equal("misc", q.Apply(g, Percentile.P50).Single().ApiName);

        q = new AchievementQuery { Search = "feather" };
        Assert.Equal(1, q.Apply(g, Percentile.P50).Count);

        q = new AchievementQuery();
        q.Kinds.Add(AchievementKind.Story);
        Assert.Equal(2, q.Apply(g, Percentile.P50).Count);
    }

    public static void Export_CsvHasBomAndEscapes()
    {
        var g = SampleGame();
        g.StateOf("misc").ManualDone = true;
        g.StateOf("misc").Note = "сделал; но \"не засчиталось\"";
        var bytes = Exporter.ToCsv(g, ExportScope.ManualOnly);
        Assert.True(bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF, "BOM");
        var text = Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        var lines = text.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        Assert.True(lines[1].Contains("\"сделал; но \"\"не засчиталось\"\"\""), lines[1]);
        Assert.True(lines[1].Contains("done_not_counted"));
    }

    public static void TagPack_RoundTrip()
    {
        var g = SampleGame();
        GameLogic.SetKind(g, "misc", AchievementKind.Missable);
        GameLogic.SetGroup(g, "ch5", "Глава 5");
        GameLogic.SetGroup(g, "ch1", ""); // a manual empty group is not exported
        g.ManualEstimates["grind"] = 120;
        var json = TagPack.From(g).ToJson();

        var g2 = SampleGame();
        var applied = TagPack.FromJson(json).ApplyTo(g2);
        Assert.Equal(3, applied);
        Assert.Equal(AchievementKind.Missable, g2.Tags["misc"].Kind);
        Assert.Equal(KindSource.Manual, g2.Tags["misc"].Source);
        Assert.Equal("Глава 5", g2.Tags["ch5"].Group);
        Assert.True(g2.Tags["ch5"].GroupIsManual, "a group from the tags file is manual");
        Assert.Equal(120.0, g2.ManualEstimates["grind"]);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public static void MergeSchema_KeepsKnownData()
    {
        var g = SampleGame();
        g.Achievements[0].SamplePercent = 42;
        g.Achievements[0].Protected = true;
        var entries = g.Achievements
            .Select(a => new AC.Infrastructure.SchemaEntry(a.ApiName, a.Name + "!", "", a.Hidden, "icon.jpg", "gray.jpg"))
            .ToList();
        AC.Infrastructure.GameService.MergeSchema(g, entries);
        Assert.Equal(42.0, g.Achievements[0].SamplePercent);
        Assert.True(g.Achievements[0].Protected);
        Assert.Equal(90.0, g.Achievements[0].GlobalPercent);
        Assert.Equal("Глава 1!", g.Achievements[0].Name);
        Assert.Equal("Пройдите главу 1", g.Achievements[0].Description); // an empty value from the Web API does not overwrite a known one
    }

    public static void Loc_PluralCategories()
    {
        string Ru(long n) => Loc.Category("ru", n);
        Assert.Equal("one", Ru(1));
        Assert.Equal("few", Ru(2));
        Assert.Equal("many", Ru(5));
        Assert.Equal("many", Ru(11));
        Assert.Equal("one", Ru(21));
        Assert.Equal("few", Ru(32));
        Assert.Equal("many", Ru(112));
        Assert.Equal("many", Ru(0));
        Assert.Equal("one", Loc.Category("en", 1));
        Assert.Equal("other", Loc.Category("en", 0));
        Assert.Equal("other", Loc.Category("en", 21));
    }

    public static void Loc_SwitchAndFallback()
    {
        Assert.Equal("en", Loc.Languages[0]);
        Assert.True(Loc.Languages.Contains("ru"), "Russian is shipped");
        int changed = 0;
        void OnChanged() => changed++;
        Loc.Changed += OnChanged;
        try
        {
            Loc.SetLanguage("ru");
            Assert.Equal("Время", Loc.T("tabs.time"));
            Assert.Equal("Загружено 21 достижение", Loc.Plural("status.gameLoaded", 21));
            Assert.Equal("russian", Loc.SteamLanguage(Loc.Language));
            Loc.SetLanguage("en");
            Assert.Equal("Time", Loc.T("tabs.time"));
            Assert.Equal("Loaded 1 achievement", Loc.Plural("status.gameLoaded", 1));
            Assert.Equal("Loaded 21 achievements", Loc.Plural("status.gameLoaded", 21));
            Assert.Equal("no.such.key", Loc.T("no.such.key"));
            Assert.Equal("ru", Loc.Resolve("ru"));
            Assert.True(Loc.Languages.Contains(Loc.Resolve("xx")), "unknown language falls back to a shipped one");
            Assert.Equal(2, changed);
        }
        finally
        {
            Loc.Changed -= OnChanged;
            Loc.SetLanguage(Loc.Fallback);
        }
    }

    /// <summary>Every translation has the English keys and placeholders, its language's plural forms and formats cleanly.</summary>
    public static void Loc_TranslationsMatchFallback()
    {
        static string Base(string key) => key.Split('#')[0];
        static HashSet<string> Holes(string s) =>
            System.Text.RegularExpressions.Regex.Matches(s, @"\{(\d+)\}").Select(m => m.Groups[1].Value).ToHashSet();
        var english = Loc.Strings(Loc.Fallback);
        foreach (var lang in Loc.Languages)
        {
            var strings = Loc.Strings(lang);
            Assert.True(english.Keys.Select(Base).ToHashSet().SetEquals(strings.Keys.Select(Base)), $"{lang}: keys differ from English");
            var forms = lang == "ru" ? new[] { "few", "many", "one" } : new[] { "one", "other" };
            foreach (var group in strings.GroupBy(kv => Base(kv.Key)))
            {
                var expected = english.Where(kv => Base(kv.Key) == group.Key).SelectMany(kv => Holes(kv.Value)).ToHashSet();
                bool plural = group.First().Key.Contains('#');
                if (plural)
                    Assert.True(group.Select(kv => kv.Key.Split('#')[1]).Order().SequenceEqual(forms), $"{lang}: {group.Key} plural forms");
                foreach (var (key, text) in group)
                {
                    var holes = Holes(text);
                    Assert.True(plural ? holes.IsSubsetOf(expected) : holes.SetEquals(expected), $"{lang}: {key} placeholders differ from English");
                    _ = string.Format(System.Globalization.CultureInfo.InvariantCulture, text, "a", "b", "c", "d");
                }
            }
        }
    }

    /// <summary>Keys used in code exist in the translation files, and the files have no extra keys.</summary>
    public static void Loc_KeysMatchCode()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "AchievementCompanion.sln"))) root = root.Parent;
        Assert.True(root != null, "repository root not found");
        var src = Path.Combine(root!.FullName, "src");
        var sections = Loc.Strings(Loc.Fallback).Keys.Select(k => k.Split('.')[0]).ToHashSet();
        var used = new HashSet<string>();
        foreach (var file in Directory.EnumerateFiles(src, "*.*", SearchOption.AllDirectories)
                     .Where(f => (f.EndsWith(".cs") || f.EndsWith(".xaml")) && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")))
        {
            var text = File.ReadAllText(file);
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(text, @"(?:""|\{local:Tr )([a-z][A-Za-z]*)\.([a-z][A-Za-z0-9]*)[""}]"))
            {
                // file names like "settings.json" look like keys
                if (m.Groups[2].Value is "json" or "jsonl" or "log" or "exe" or "dll" or "bin" or "vdf") continue;
                if (sections.Contains(m.Groups[1].Value)) used.Add(m.Groups[1].Value + "." + m.Groups[2].Value);
            }
        }
        foreach (var lang in Loc.Languages)
        {
            var keys = Loc.Strings(lang).Keys.Select(k => k.Split('#')[0]).ToHashSet();
            var missing = used.Where(k => !keys.Contains(k)).ToList();
            Assert.True(missing.Count == 0, $"{lang}: missing keys {string.Join(", ", missing)}");
            var unused = keys.Where(k => !used.Contains(k)).ToList();
            Assert.True(unused.Count == 0, $"{lang}: unused keys {string.Join(", ", unused)}");
        }
    }

    public static void BinaryKeyValue_SchemaParse()
    {
        // Binary KV built by hand: { "480" { "stats" { "1" { type=4 bits { "0" { name, display{...}, permission } } } } } }
        var ms = new MemoryStream();
        void B(byte b) => ms.WriteByte(b);
        void S(string s) { var x = Encoding.UTF8.GetBytes(s); ms.Write(x); B(0); }
        void Str(string k, string v) { B(1); S(k); S(v); }
        void Int(string k, int v) { B(2); S(k); ms.Write(BitConverter.GetBytes(v)); }
        void Open(string k) { B(0); S(k); }
        void End() => B(8);

        Open("480"); Open("stats"); Open("1");
        Int("type_int", 4);
        Open("bits");
        Open("0");
        Str("name", "ACH_WIN");
        Int("permission", 2);
        Open("display");
        Open("name"); Str("english", "Winner"); Str("russian", "Победитель"); End();
        Open("desc"); Str("english", "Win a game"); End();
        Int("hidden", 1);
        Str("icon", "abc.jpg");
        End(); // display
        End(); // 0
        End(); // bits
        End(); // 1
        End(); // stats
        End(); // 480
        End(); // root

        ms.Position = 0;
        var root = BinaryKeyValue.Read(ms);
        var list = StatsSchemaReader.Read(root, 480, "russian");
        Assert.Equal(1, list.Count);
        Assert.Equal("ACH_WIN", list[0].ApiName);
        Assert.Equal("Победитель", list[0].Name);
        Assert.Equal("Win a game", list[0].Description);
        Assert.True(list[0].Hidden);
        Assert.True(list[0].Protected);
        Assert.Equal("https://cdn.steamstatic.com/steamcommunity/public/images/apps/480/abc.jpg", StatsSchemaReader.IconUrl(480, list[0].Icon));
    }

    public static void SteamClient_ParseCommand()
    {
        var read = AC.Infrastructure.BridgeCommand.Parse(["read", "480", "--lang", "russian", "--ui", "ru"]);
        Assert.Equal(AC.Infrastructure.BridgeAction.Read, read.Action);
        Assert.Equal(480u, read.AppId);
        Assert.Equal("russian", read.Language);
        Assert.Equal("ru", read.UiLanguage);
        Assert.Equal(0, read.Names.Count);

        var unlock = AC.Infrastructure.BridgeCommand.Parse(["UNLOCK", "480", "A", "--lang", "english", "B", "A"]);
        Assert.Equal(AC.Infrastructure.BridgeAction.Unlock, unlock.Action);
        Assert.Equal("A,B", string.Join(",", unlock.Names));

        foreach (string[] bad in (string[][])[["read"], ["fly", "480"], ["read", "-1"], ["read", "0"], ["read", "4x"], ["lock", "480"]])
        {
            bool threw = false;
            try { AC.Infrastructure.BridgeCommand.Parse(bad); }
            catch (ArgumentException) { threw = true; }
            Assert.True(threw, "accepted " + string.Join(" ", bad));
        }
    }

    public static void Vdf_LoginUsers()
    {
        const string vdf = """
        "users"
        {
            "76561198000000001"
            {
                "AccountName"   "old"
                "PersonaName"   "Old"
                "MostRecent"    "0"
            }
            // comment
            "76561198000000002"
            {
                "AccountName"   "usus"
                "PersonaName"   "Usus \"Cimex\""
                "MostRecent"    "1"
            }
        }
        """;
        var u = TextVdf.MostRecentUser(vdf);
        Assert.Equal(76561198000000002UL, u!.Value.SteamId);
        Assert.Equal("Usus \"Cimex\"", u.Value.PersonaName);
    }

    public static void Completionist_ParseMedian()
    {
        const string html = """
        <div class="stat"><span class="value">22h 20m</span><span class="label">Completion Playtime Median</span></div>
        <div class="stat"><span class="value">32h 47m</span><span class="label">Completion Playtime Avg.</span></div>
        """;
        Assert.Equal(22 * 60 + 20.0, AC.Infrastructure.CompletionTimeSource.ParseMedian(html));
        Assert.Equal((double?)null, AC.Infrastructure.CompletionTimeSource.ParseMedian("<p>nothing</p>"));
        Assert.Equal(45.0, AC.Infrastructure.CompletionTimeSource.ParseMedian("<b>Completion Playtime Median</b> <i>45m</i>"));
    }

    public static void Library_SkipsComputedUrls()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ac-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new AC.Infrastructure.JsonStore(dir);
            store.SaveLibrary(new List<AC.Infrastructure.OwnedGame> { new() { AppId = 480, Name = "Spacewar", IconHash = "abc" } });
            var json = File.ReadAllText(Path.Combine(dir, "library.json"));
            Assert.True(!json.Contains("HeaderUrl") && !json.Contains("IconUrl"), "image URLs are computed, not written to the file");
            Assert.Equal("abc", store.LoadLibrary()!.Single().IconHash);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    public static void JsonStore_RoundTripAndLog()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ac-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new AC.Infrastructure.JsonStore(dir);
            var g = SampleGame();
            g.StateOf("misc").ManualDone = true;
            g.StateOf("misc").Note = "заметка";
            g.PendingUnlocks.Add("misc");
            GameLogic.SetKind(g, "ch1", AchievementKind.Grind);
            g.Tags["grind"] = new AchievementTag { Kind = AchievementKind.Grind, Group = "Старая группа" }; // old format, no GroupSource
            store.SaveGame(g);

            var back = store.LoadGame(1)!;
            Assert.Equal(5, back.Achievements.Count);
            Assert.True(back.States["misc"].ManualDone);
            Assert.Equal("заметка", back.States["misc"].Note);
            Assert.Equal(AchievementKind.Grind, back.Tags["ch1"].Kind);
            Assert.Equal(KindSource.Manual, back.Tags["ch1"].Source);
            Assert.Equal("misc", back.PendingUnlocks.Single());
            Assert.Equal(KindSource.Auto, back.Tags["ch5"].GroupSource!.Value);
            Assert.True(back.Tags["grind"].GroupSource is null && back.Tags["grind"].GroupIsManual, "old file: the group is manual");

            // the library summary is updated on save and rebuilt if its file is missing
            var summary = new AC.Infrastructure.GameSummary { Unlocked = 0, Manual = 1, Total = 5 };
            Assert.Equal(summary, store.LoadSummaries()[1]);
            File.Delete(Path.Combine(dir, "summary.json"));
            Assert.Equal(summary, new AC.Infrastructure.JsonStore(dir).LoadSummaries()[1]);

            var s = store.LoadSettings();
            s.SteamId64 = 76561198000000002UL;
            s.WriteMode = AC.Infrastructure.WriteMode.Queue;
            store.SaveSettings(s);
            var s2 = store.LoadSettings();
            Assert.Equal(76561198000000002UL, s2.SteamId64);
            Assert.Equal(AC.Infrastructure.WriteMode.Queue, s2.WriteMode);
            Assert.Equal(';', s2.CsvSeparator);

            store.AppendLog(new AC.Infrastructure.UnlockLogEntry { At = DateTimeOffset.Now, AppId = 1, ApiName = "misc", Action = "unlock", Ok = true });
            store.AppendLog(new AC.Infrastructure.UnlockLogEntry { At = DateTimeOffset.Now, AppId = 2, ApiName = "x", Action = "unlock", Ok = false, Error = "e" });
            Assert.Equal(1, store.ReadLog(1).Count);
            Assert.Equal(2, store.ReadLog().Count);

            File.WriteAllText(store.GamePath(3), "{ broken");
            Assert.True(store.LoadGame(3) is null, "a broken file loads as null");
            Assert.True(Directory.GetFiles(Path.Combine(dir, "games"), "3.json.broken-*").Length == 1, "a copy of the broken file");
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    public static void Playtime_OverrideAndOffset()
    {
        var g = SampleGame();
        g.PlaytimeMinutes = 6000; // 100 h in Steam, but achievements were added later
        g.StateOf("ch5").SteamUnlocked = true;
        var slow = Planner.Build(g, Percentile.P50);
        Assert.Equal(Planner.MaxPace, slow.Pace);

        g.PlaytimeOffsetMinutes = 5900; // 98 h 20 min were before achievements
        Assert.Equal(100.0, g.EffectivePlaytimeMinutes);
        var withOffset = Planner.Build(g, Percentile.P50);
        Assert.Equal(1.0, withOffset.Pace);
        Assert.True(withOffset.RemainingMinutes < slow.RemainingMinutes, "the offset removes the slow pace");

        g.PlaytimeOverrideMinutes = 400; // the override beats the offset
        Assert.Equal(400.0, g.EffectivePlaytimeMinutes);
        Assert.True(Planner.Build(g, Percentile.P50).Pace > 2, "400 minutes to ch5 is slow");

        g.PlaytimeOffsetMinutes = 99999;
        g.PlaytimeOverrideMinutes = null;
        Assert.Equal(0.0, g.EffectivePlaytimeMinutes);
    }

    public static void Progress_IdleStretchesDoNotSlowDown()
    {
        var now = DateTimeOffset.Now;
        var g = SampleGame();
        g.StateOf("ch5").SteamUnlocked = true;
        g.StateOf("ch5").SteamUnlockTime = now.AddDays(-10);
        g.PlaytimeMinutes = 3000;
        // Without history all 3000 minutes went into reaching ch5: the slowest pace.
        Assert.Equal(Planner.MaxPace, Planner.Build(g, Percentile.P50, now).Pace);

        // Steam showed 200 minutes the day after ch5; the next 2800 brought nothing: idle, not slow.
        g.PlaytimeHistory.Add(new PlaytimePoint { At = now.AddDays(-9), Minutes = 200 });
        var plan = Planner.Build(g, Percentile.P50, now);
        Assert.True(plan.Pace is > 1.1 and < 1.2, $"pace {plan.Pace}");

        // The time to 100% derived from own progress skips the idle hours too.
        g.CompletionMinutes = null;
        TimeEstimator.EstimateFromGlobalPercent(g);
        Assert.True(g.DerivedCompletionMinutes is > 650 and < 750, $"derived {g.DerivedCompletionMinutes}");
    }

    public static void Estimator_DerivedFromOwnProgress()
    {
        var g = SampleGame();
        g.CompletionMinutes = null;
        g.PlaytimeMinutes = 300;
        TimeEstimator.EstimateFromGlobalPercent(g);
        Assert.Equal(0, g.Estimates.Count); // nothing earned - nothing to derive from

        g.StateOf("ch5").SteamUnlocked = true; // 30% is roughly the middle of the scale
        TimeEstimator.EstimateFromGlobalPercent(g);
        Assert.True(g.DerivedCompletionMinutes is > 300, $"derived {g.DerivedCompletionMinutes}");
        Assert.True(g.Estimates.Count == 5, "estimates appeared");
        Assert.Equal(g.DerivedCompletionMinutes, g.UsedCompletionMinutes);

        g.CompletionMinutes = 1000; // a set value beats a derived one
        TimeEstimator.EstimateFromGlobalPercent(g);
        Assert.Equal((double?)null, g.DerivedCompletionMinutes);
        Assert.Equal(1000.0, g.Estimates["grind"].P50!.Value);
    }

    public static void SampleStats_PercentAndTimes()
    {
        var t0 = DateTimeOffset.FromUnixTimeSeconds(1_760_000_000);
        PlayerSample P(int hoursTotal, params (string Name, double HoursIn)[] unl)
        {
            // every real hour is exactly 60 minutes of play, to check the conversion
            var p = new PlayerSample();
            foreach (var (n, h) in unl) p.Unlocks[n] = t0.AddHours(h);
            p.Anchors.Add((t0.AddHours(hoursTotal), hoursTotal * 60));
            return p;
        }
        var players = new List<PlayerSample>
        {
            P(10, ("a", 0), ("b", 2), ("c", 10)),
            P(10, ("a", 0), ("b", 4), ("c", 8)),
            P(5, ("a", 0), ("b", 3)),
            P(5, ("a", 0)),
        };
        var names = new[] { "a", "b", "c" };
        var r = SampleStats.Compute(players, names);
        Assert.Equal(4, r.Players);
        Assert.Equal(100.0, r.Percent["a"]);
        Assert.Equal(75.0, r.Percent["b"]);
        Assert.Equal(50.0, r.Percent["c"]);
        Assert.Equal(180.0, r.Times["b"].P50!.Value); // median of 2, 3 and 4 hours
        Assert.Equal(2, r.Completers);
        Assert.Equal(540.0, r.CompletionMedianMinutes); // 8 and 10 hours

        var g = SampleGame();
        g.Achievements.Clear();
        foreach (var n in names) g.Achievements.Add(new AchievementDef { ApiName = n, Name = n });
        g.CompletionMinutes = null;
        SampleStats.Apply(g, r, DateTimeOffset.Now);
        TimeEstimator.EstimateFromGlobalPercent(g);
        Assert.Equal(75.0, g.Achievements[1].Percent);
        Assert.True(g.Achievements[1].PercentFromSample);
        Assert.Equal(180.0, g.Estimates["b"].P50!.Value); // the model did not overwrite the measurement
        Assert.Equal(SampleStats.SourceName, g.Estimates["b"].Source);
        Assert.Equal(540.0, g.UsedCompletionMinutes);
    }
}
