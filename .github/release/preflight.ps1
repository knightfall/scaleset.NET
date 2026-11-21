param([string]$CandidateRunId)

$ErrorActionPreference = 'Stop'
$policy = Get-Content "$PSScriptRoot/policy.json" -Raw | ConvertFrom-Json
$tag = "v$($policy.version)"
$reference = gh api "repos/$($policy.repository)/git/ref/tags/$tag" | ConvertFrom-Json
if ($LASTEXITCODE -ne 0 -or $reference.object.type -ne 'commit' -or $reference.object.sha -ne $env:GITHUB_SHA) {
    throw 'The protected lightweight release tag must identify this exact source commit'
}

if ($CandidateRunId) {
    if ($CandidateRunId -cnotmatch '^[0-9]+\z') { throw 'Invalid candidate_run_id' }
    $runPath = "repos/$($policy.repository)/actions/runs/$CandidateRunId"
    $run = gh api $runPath | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0 -or $run.path -ne '.github/workflows/publish.yaml' -or
        $run.head_repository.full_name -ne $policy.repository -or $run.head_branch -ne 'main' -or
        $run.head_sha -ne $env:GITHUB_SHA -or $run.event -notin @('push', 'workflow_dispatch')) {
        throw 'Reused candidate run must be from publish.yaml on main at this exact commit'
    }
    $jobs = gh api "$runPath/jobs" | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0) { throw 'Cannot read reused candidate jobs' }
    $buildJob = $jobs.jobs | Where-Object { $_.name -eq 'Build release candidate' } | Select-Object -First 1
    if (!$buildJob -or $buildJob.status -ne 'completed' -or $buildJob.conclusion -ne 'success') {
        throw 'Reused candidate run must have a completed successful Build release candidate job'
    }
}

$checks = gh api "repos/$($policy.repository)/commits/$env:GITHUB_SHA/check-runs?per_page=100" | ConvertFrom-Json
if ($LASTEXITCODE -ne 0) { throw 'Cannot read source verification checks' }
foreach ($required in $policy.requiredChecks.PSObject.Properties) {
    # A retry skips its own build. Use the successful job verified in the selected run.
    if ($CandidateRunId -and $required.Name -eq 'Build release candidate') { continue }
    $check = $checks.check_runs | Where-Object { $_.name -eq $required.Name -and $_.app.slug -eq $required.Value } | Select-Object -First 1
    if (!$check -or $check.status -ne 'completed' -or $check.conclusion -ne 'success') {
        throw "Required source verification is not successful: $($required.Name)"
    }
}
