using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Xunit;

namespace TidyC.Core.TestRunner;

/// <summary>
/// In-process test runner. It executes every xunit [Fact] from the test assembly
/// inside a single process, which keeps working in environments where the VSTest
/// testhost cannot open its parent process handle (restricted sandboxes) — the
/// xunit project itself remains the canonical `dotnet test` entry point.
/// </summary>
public static class Program
{
    public static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        string? filter = null;
        string? reportPath = null;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] is "--filter" or "-f" && i + 1 < args.Length)
            {
                filter = args[++i];
            }
            else if (args[i] is "--report" or "-r" && i + 1 < args.Length)
            {
                reportPath = args[++i];
            }
        }

        Assembly assembly = typeof(TidyC.Core.Tests.RuleValidatorTests).Assembly;
        var results = new List<TestOutcome>();
        var total = Stopwatch.StartNew();

        foreach (Type type in assembly.GetTypes()
                     .Where(t => t is { IsClass: true, IsAbstract: false } &&
                                 t.Namespace is not null &&
                                 t.Namespace.StartsWith("TidyC.Core.Tests", StringComparison.Ordinal))
                     .OrderBy(t => t.FullName, StringComparer.Ordinal))
        {
            MethodInfo[] methods = type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Where(m => m.GetCustomAttributes().Any(a => a is FactAttribute))
                .OrderBy(m => m.Name, StringComparer.Ordinal)
                .ToArray();

            if (methods.Length == 0)
            {
                continue;
            }

            foreach (MethodInfo method in methods)
            {
                string name = $"{type.Name}.{method.Name}";
                if (filter is not null && !name.Contains(filter, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var outcome = new TestOutcome { Name = name };
                var stopwatch = Stopwatch.StartNew();
                object? instance = null;

                try
                {
                    instance = Activator.CreateInstance(type);
                    method.Invoke(instance, null);
                    outcome.Passed = true;
                }
                catch (TargetInvocationException ex)
                {
                    outcome.Passed = false;
                    Exception inner = ex.InnerException ?? ex;
                    outcome.Error = inner.GetType().Name + ": " + inner.Message;
                    outcome.StackTrace = inner.StackTrace;
                }
                catch (Exception ex)
                {
                    outcome.Passed = false;
                    outcome.Error = ex.GetType().Name + ": " + ex.Message;
                    outcome.StackTrace = ex.StackTrace;
                }
                finally
                {
                    (instance as IDisposable)?.Dispose();
                    stopwatch.Stop();
                    outcome.DurationMs = Math.Round(stopwatch.Elapsed.TotalMilliseconds, 1);
                }

                results.Add(outcome);
                Console.WriteLine($"{(outcome.Passed ? "PASS" : "FAIL")}  {name}  ({outcome.DurationMs:0.0} ms)");
                if (!outcome.Passed)
                {
                    Console.WriteLine("      " + outcome.Error);
                }
            }
        }

        total.Stop();
        int passed = results.Count(r => r.Passed);
        int failed = results.Count - passed;

        Console.WriteLine();
        Console.WriteLine($"总计 {results.Count} 项测试 · 通过 {passed} · 失败 {failed} · 耗时 {total.Elapsed.TotalSeconds:0.00}s");

        if (failed > 0)
        {
            Console.WriteLine();
            Console.WriteLine("失败详情：");
            foreach (TestOutcome outcome in results.Where(r => !r.Passed))
            {
                Console.WriteLine($"  - {outcome.Name}: {outcome.Error}");
                if (!string.IsNullOrEmpty(outcome.StackTrace))
                {
                    Console.WriteLine("      " + outcome.StackTrace.Split('\n').FirstOrDefault()?.Trim());
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(reportPath))
        {
            string json = JsonSerializer.Serialize(
                new
                {
                    generated_at = DateTime.Now,
                    total = results.Count,
                    passed,
                    failed,
                    duration_seconds = Math.Round(total.Elapsed.TotalSeconds, 3),
                    tests = results,
                },
                new JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                });

            File.WriteAllText(reportPath, json);
            Console.WriteLine($"报告已写入：{reportPath}");
        }

        return failed == 0 ? 0 : 1;
    }
}

public sealed class TestOutcome
{
    public string Name { get; set; } = string.Empty;

    public bool Passed { get; set; }

    public string? Error { get; set; }

    public string? StackTrace { get; set; }

    public double DurationMs { get; set; }
}
