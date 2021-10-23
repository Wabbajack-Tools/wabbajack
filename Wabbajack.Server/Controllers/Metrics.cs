﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Nettle;
using Wabbajack.Common;
using Wabbajack.Server;
using Wabbajack.Server.DataLayer;
using Wabbajack.Server.DTOs;
using Wabbajack.Server.Services;

namespace Wabbajack.BuildServer.Controllers;

[ApiController]
[Route("/metrics")]
public class MetricsController : ControllerBase
{
    private static readonly Func<object, string> ReportTemplate = NettleEngine.GetCompiler().Compile(@"
            <html><body>
                <h2>Tar Report for {{$.key}}</h2>
                <h3>Ban Status: {{$.status}}</h3>
                <table>
                {{each $.log }}
                <tr>
                <td>{{$.Timestamp}}</td>
                <td>{{$.Path}}</td>
                <td>{{$.Key}}</td>
                </tr>
                {{/each}}
                </table>
            </body></html>
        ");

    private static Func<object, string> _totalListTemplate;
    private readonly AppSettings _settings;
    private readonly MetricsKeyCache _keyCache;
    private ILogger<MetricsController> _logger;
    private readonly SqlService _sql;

    public MetricsController(ILogger<MetricsController> logger, SqlService sql, MetricsKeyCache keyCache,
        AppSettings settings)
    {
        _sql = sql;
        _logger = logger;
        _keyCache = keyCache;
        _settings = settings;
    }


    private static Func<object, string> TotalListTemplate
    {
        get
        {
            if (_totalListTemplate == null)
            {
                var resource = Assembly.GetExecutingAssembly()
                    .GetManifestResourceStream("Wabbajack.Server.Controllers.Templates.TotalListTemplate.html")!
                    .ReadAllText();
                _totalListTemplate = NettleEngine.GetCompiler().Compile(resource);
            }

            return _totalListTemplate;
        }
    }

    [HttpGet]
    [Route("{subject}/{value}")]
    public async Task<Result> LogMetricAsync(string subject, string value)
    {
        var date = DateTime.UtcNow;
        var metricsKey = Request.Headers[_settings.MetricsKeyHeader].FirstOrDefault();
        if (metricsKey != null)
            await _keyCache.AddKey(metricsKey);

        // Used in tests
        if (value == "Default" || value == "untitled" || subject == "failed_download" || Guid.TryParse(value, out _))
            return new Result {Timestamp = date};

        await Log(date, subject, value, metricsKey);
        return new Result {Timestamp = date};
    }

    [HttpGet]
    [Route("report/{subject}")]
    [ResponseCache(Duration = 60 * 60)]
    public async Task<IActionResult> MetricsReport(string subject)
    {
        var metrics = (await _sql.MetricsReport(subject)).ToList();
        var labels = metrics.GroupBy(m => m.Date)
            .OrderBy(m => m.Key)
            .Select(m => m.Key)
            .ToArray();
        var labelStrings = labels.Select(l => l.ToString("MM-dd-yyy")).ToList();
        var results = metrics
            .GroupBy(m => m.Subject)
            .Select(g =>
            {
                var indexed = g.ToDictionary(m => m.Date, m => m.Count);
                return new MetricResult
                {
                    SeriesName = g.Key,
                    Labels = labelStrings,
                    Values = labels.Select(l => indexed.TryGetValue(l, out var found) ? found : 0).ToList()
                };
            });
        return Ok(results.ToList());
    }

    [HttpGet]
    [Route("badge/{name}/total_installs_badge.json")]
    public async Task<IActionResult> TotalInstallsBadge(string name)
    {
        var results = await _sql.TotalInstalls(name);

        Response.ContentType = "application/json";

        return Ok(results == 0
            ? new Badge($"Modlist {name} not found!", "Error") {color = "red"}
            : new Badge("Installations: ", "____") {color = "green"});
    }

    [HttpGet]
    [Route("badge/{name}/unique_installs_badge.json")]
    public async Task<IActionResult> UniqueInstallsBadge(string name)
    {
        var results = await _sql.UniqueInstalls(name);

        Response.ContentType = "application/json";

        return Ok(results == 0
            ? new Badge($"Modlist {name} not found!", "Error") {color = "red"}
            : new Badge("Installations: ", "____") {color = "green"});
    }

    [HttpGet]
    [Route("tarlog/{key}")]
    public async Task<IActionResult> TarLog(string key)
    {
        var isTarKey = await _sql.IsTarKey(key);

        var report = new List<(DateTime, string, string)>();

        if (isTarKey) report = await _sql.FullTarReport(key);

        var response = ReportTemplate(new
        {
            key,
            status = isTarKey ? "BANNED" : "NOT BANNED",
            log = report.Select(entry => new
            {
                Timestamp = entry.Item1,
                Path = entry.Item2,
                Key = entry.Item3
            }).ToList()
        });
        return new ContentResult
        {
            ContentType = "text/html",
            StatusCode = (int) HttpStatusCode.OK,
            Content = response
        };
    }

    private async Task Log(DateTime timestamp, string action, string subject, string metricsKey = null)
    {
        //_logger.Log(LogLevel.Information, $"Log - {timestamp} {action} {subject} {metricsKey}");
        await _sql.IngestMetric(new Metric
        {
            Timestamp = timestamp, Action = action, Subject = subject, MetricsKey = metricsKey
        });
    }


    [HttpGet("total_installs.html")]
    [ResponseCache(Duration = 60 * 60)]
    public async Task<ContentResult> TotalInstalls()
    {
        var data = await _sql.GetTotalInstalls();
        var result = TotalListTemplate(new TotalListTemplateData
        {
            Title = "Total Installs",
            Total = data.Sum(d => d.Item2),
            Items = data.Select(d => new TotalListTemplateData.Item {Title = d.Item1, Count = d.Item2})
                .ToArray()
        });
        return new ContentResult
        {
            ContentType = "text/html",
            StatusCode = (int) HttpStatusCode.OK,
            Content = result
        };
    }

    [HttpGet("total_unique_installs.html")]
    [ResponseCache(Duration = 60 * 60)]
    public async Task<ContentResult> TotalUniqueInstalls()
    {
        var data = await _sql.GetTotalUniqueInstalls();
        var result = TotalListTemplate(new TotalListTemplateData
        {
            Title = "Total Unique Installs",
            Total = data.Sum(d => d.Item2),
            Items = data.Select(d => new TotalListTemplateData.Item {Title = d.Item1, Count = d.Item2})
                .ToArray()
        });
        return new ContentResult
        {
            ContentType = "text/html",
            StatusCode = (int) HttpStatusCode.OK,
            Content = result
        };
    }

    [HttpGet("dump.json")]
    public async Task<IActionResult> DataDump()
    {
        return Ok(await _sql.MetricsDump().ToArrayAsync());
    }

    public class Result
    {
        public DateTime Timestamp { get; set; }
    }

    private class TotalListTemplateData
    {
        public string Title { get; set; }
        public long Total { get; set; }
        public Item[] Items { get; set; }

        public class Item
        {
            public long Count { get; set; }
            public string Title { get; set; }
        }
    }
}