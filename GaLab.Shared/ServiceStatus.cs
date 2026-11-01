namespace GaLab.Shared;

public sealed record JobProgress(string JobId, int Generation, int TotalGenerations, double? BestLength);

public sealed record ServiceStatusResponse(string InstanceId, JobProgress[] ActiveJobs);
