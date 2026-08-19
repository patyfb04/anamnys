// PipelineJob has been moved to Anamnys.Api/Jobs/PipelineJob.cs.
// It lives in the Api layer so it can reference the real SignalR hub type
// without creating a circular project dependency.
//
// This file is intentionally empty — kept so the folder structure is preserved
// and Hangfire enqueue calls can still reference Anamnys.Api.Jobs.PipelineJob.
