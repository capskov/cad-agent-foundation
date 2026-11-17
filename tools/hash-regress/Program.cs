using System;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Bimwright.Dwg.Shared.Execution;

class Program
{
    static int fails = 0;

    static void Check(string label, string actual, string expected)
    {
        bool ok = string.Equals(actual, expected, StringComparison.Ordinal);
        Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {label}");
        if (!ok)
        {
            Console.WriteLine($"        expected {expected}");
            Console.WriteLine($"        actual   {actual}");
            fails++;
        }
    }

    static void Main(string[] args)
    {
        string dir = args.Length > 0 ? args[0] : Path.Combine(AppContext.BaseDirectory, "samples");

        // 1) entity digest recorded by the net48 plugin in the pilot ledger
        var entity = JObject.Parse(File.ReadAllText(Path.Combine(dir, "entity-ws15.json")));
        Check("Hash(entity 24B) == ledger entity_digest",
            FoundationDrawingPlan.Hash(entity),
            "9D378023F1F4C62E3A70F5FE2DB93EE2A033A440E49C251FF9A100824C6E9CA8");

        // 2) plan digest recorded by the net48 plugin
        var plan = JObject.Parse(File.ReadAllText(Path.Combine(dir, "plan.json")));
        Check("Hash(plan-v6d) == receipt plan_sha256",
            FoundationDrawingPlan.Hash(plan),
            "D32EDDE6195D88E6ACB7901D617F325856E117CAB3AD31938C8F8FEC38018B23");

        // 3) the receipt's before snapshot must hash to the same digest the plan carried,
        //    which is exactly what the server failed to reproduce before the fix
        var receipt = JObject.Parse(File.ReadAllText(Path.Combine(dir, "receipt.json")));
        Check("Hash(receipt.before.24B) == plan expected_sha256",
            FoundationDrawingPlan.Hash(receipt["before"]["24B"]),
            plan["steps"][0]["expected_sha256"].Value<string>());

        // 4) number formatting must match the net48 Canonical text byte for byte
        var probes = new (double, string)[]
        {
            (0.1, "0.1"),
            (1.0 / 3.0, "0.33333333333333331"),
            (5619.8324022346369, "5619.8324022346369"),
            (300.0, "300.0"),
            (1e-7, "1E-07"),
            (1e20, "1E+20"),
            (-0.05, "-0.05"),
            (0.000123, "0.000123"),
            (2.5, "2.5"),
            (0.0, "0.0"),
            (200.0, "200.0"),
        };
        foreach (var (value, expected) in probes)
            Check($"number {value}", FoundationDrawingPlan.NumberText(value), expected);

        // 5) escaping must match Newtonsoft default (quotes, backslashes, control chars, raw CJK)
        var esc = new JObject
        {
            ["q"] = "he said \"hi\"",
            ["bs"] = "DN500\\/DN400",
            ["ctl"] = "a\u0001b\tc\nd",
            ["cjk"] = "三管共用钢筋混凝土闸阀井"
        };
        Check("escape round-trip", FoundationDrawingPlan.Canonical(esc),
            "{\"bs\":\"DN500\\\\/DN400\",\"cjk\":\"三管共用钢筋混凝土闸阀井\",\"ctl\":\"a\\u0001b\\tc\\nd\",\"q\":\"he said \\\"hi\\\"\"}");

        // 6) the canonical text of the real entity must equal the net48 dump
        string oraclePath = Path.Combine(dir, "canon-entity-net48.txt");
        if (File.Exists(oraclePath))
        {
            string oracle = File.ReadAllText(oraclePath).Trim();
            Check("Canonical(entity) == net48 canonical text",
                FoundationDrawingPlan.Canonical(entity), oracle);
        }
        else Console.WriteLine("SKIP  no net48 canonical oracle file");

        Console.WriteLine(fails == 0 ? "\nALL CHECKS PASSED" : $"\n{fails} CHECK(S) FAILED");
        Environment.Exit(fails == 0 ? 0 : 1);
    }
}
