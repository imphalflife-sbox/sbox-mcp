namespace SboxMcp.Bridge.Diagnostics;

internal sealed record DiagnosticEntry(
	string Severity,    // "Error" | "Warning" | "Info" | "Hidden"
	string Id,          // e.g. "CS1002", "SB1000"
	string Message,
	string FilePath,    // empty if no location
	int Line,           // 1-based; 0 if no location
	int Column          // 1-based; 0 if no location
);

internal sealed record CompileSnapshot(
	string CompletedAt,            // ISO-8601
	bool Success,
	int ErrorCount,
	int WarningCount,
	DiagnosticEntry[] Diagnostics
);
