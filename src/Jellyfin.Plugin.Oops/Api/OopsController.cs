using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Mime;
using Jellyfin.Plugin.Oops.Transfer;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Oops.Api;

/// <summary>
/// OOPS API. Every endpoint requires an administrator – regular users get 403.
/// </summary>
[ApiController]
[Route("OOPS")]
[Authorize(Policy = Policies.RequiresElevation)]
[Produces(MediaTypeNames.Application.Json)]
public class OopsController : ControllerBase
{
    private readonly TransferService _transferService;

    /// <summary>
    /// Initializes a new instance of the <see cref="OopsController"/> class.
    /// </summary>
    /// <param name="transferService">Transfer service.</param>
    public OopsController(TransferService transferService)
    {
        _transferService = transferService;
    }

    /// <summary>
    /// Lists the libraries the given items can be moved to.
    /// </summary>
    /// <param name="ids">Comma-separated item ids.</param>
    /// <returns>Items and eligible target libraries.</returns>
    [HttpGet("Targets")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<TargetsResponse> GetTargets([FromQuery] string ids)
    {
        var parsed = ParseIds(ids);
        if (parsed.Count == 0)
        {
            return BadRequest("No item ids given.");
        }

        return _transferService.GetTargets(parsed);
    }

    /// <summary>
    /// Starts moving items to another library. Runs in the background.
    /// </summary>
    /// <param name="request">Items and target library.</param>
    /// <returns>The job id to poll.</returns>
    [HttpPost("Transfer")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<TransferStartedResponse> Transfer([FromBody] TransferRequest request)
    {
        if (request.ItemIds.Count == 0)
        {
            return BadRequest("No item ids given.");
        }

        try
        {
            var jobId = _transferService.StartTransfer(request.ItemIds, request.TargetLibraryId, request.TargetFolder);
            return new TransferStartedResponse { JobId = jobId };
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    /// <summary>
    /// Gets a transfer job's progress.
    /// </summary>
    /// <param name="jobId">Job id.</param>
    /// <returns>Job status.</returns>
    [HttpGet("Jobs/{jobId}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<TransferJobDto> GetJob([FromRoute] Guid jobId)
    {
        var job = _transferService.GetJob(jobId);
        if (job is null)
        {
            return NotFound();
        }

        return job;
    }

    /// <summary>
    /// Lists recent transfer jobs (kept in memory until Jellyfin restarts).
    /// </summary>
    /// <returns>Recent jobs.</returns>
    [HttpGet("Jobs")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<TransferJobDto>> GetJobs()
    {
        return Ok(_transferService.GetJobs());
    }

    private static List<Guid> ParseIds(string? ids)
    {
        if (string.IsNullOrWhiteSpace(ids))
        {
            return new List<Guid>();
        }

        return ids.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => Guid.TryParse(s, out var g) ? g : Guid.Empty)
            .Where(g => g != Guid.Empty)
            .Distinct()
            .ToList();
    }
}
