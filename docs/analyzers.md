# Analyzer policy

All five .NET projects use the SDK's `latest-all` analysis, build-time code style,
nullable analysis, and the private, stable analyzer versions pinned in
`Directory.Build.props`. The test project additionally uses xUnit's analyzer.
Hyperlinq covers the solution's LINQ and custom enumeration. Banned API analysis
enforces `BannedSymbols.txt`, which prevents native allocations outside NAM's
ownership and lease checks. Analyzer crashes fail validation.

The libraries are components of the engine application, not published NuGet
packages. `IsPackable` is false. Public API analysis must be added before creating
a published package with a maintained API.

Correctness, security, reliability, threading, disposal, usage and performance
rules are warnings or errors. No project or diagnostic category is suppressed.
When a rule is an exact duplicate, the SDK rule takes precedence. The disabled MA
rules and their enabled SDK equivalents are:

| MA rule | SDK rule |
| --- | --- |
| MA0001, MA0074 | CA1307, CA1309, CA1310 |
| MA0005 | CA1825 |
| MA0010 | CA1018 |
| MA0011 | CA1304, CA1305 |
| MA0012, MA0014 | CA2201 |
| MA0013 | CA1058 |
| MA0027 | CA2200 |
| MA0028 | CA1830, CA1834 |
| MA0033 | CA2259 |
| MA0039 | CA5359 |
| MA0043 | CA1507 |
| MA0047 | CA1050 |
| MA0049 | CA1724 |
| MA0057, MA0058, MA0059 | CA1710 |
| MA0062 | CA2217 |
| MA0069 | CA2211 |
| MA0070 | CA1041 |
| MA0072 | CA2219 |
| MA0082 | CA2242 |
| MA0086 | CA1065 |
| MA0095 | CA1067 |
| MA0097 | CA1036 |
| MA0112 | CA1860 |
| MA0131 | CA2264 |

Equivalence follows the [MA comparison documentation](https://github.com/meziantou/Meziantou.Analyzer/blob/main/docs/comparison-with-other-analyzers.md).
Similar rules with additional coverage, including MA0015, MA0020, MA0036,
MA0004 and the cancellation rules, remain enabled at their applicable severity.

Any necessary suppression belongs on the smallest affected member or type and
must explain its technical reason. Validation reports record its evidence.
The Release review gate requires zero unresolved enabled warnings or errors,
followed by the applicable headless correctness and allocation checks.

Native enum predicates retain MA0192's `HasFlag` form. On the pinned .NET 10
runtime, Tier0 boxes its enum operands; a small native descriptor predicate uses
`AggressiveOptimization` to avoid those cold allocations. Player movement uses
the same compilation policy. The allocation control and full production gate
verify this choice; no analyzer rule is suppressed for it. The runtime's
[Tier0 box-elision fix](https://github.com/dotnet/runtime/pull/130590) is tracked
for a future runtime upgrade.
