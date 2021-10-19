using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using SnapSecret.Application.Abstractions;
using System.Text.Json;
using SnapSecret.Domain;
using System.ComponentModel.DataAnnotations;
using System.Net;

namespace SnapSecret.AzureFunctions
{
    public class SecretsFunctions
    {
        private const string SecretsBasePath = "v1/secrets";

        private readonly ISnapSecretBusinessLogic _snapSecretBusinessLogic;

        public SecretsFunctions(ISnapSecretBusinessLogic snapSecretBusinessLogic)
        {
            _snapSecretBusinessLogic = snapSecretBusinessLogic;
        }

        [FunctionName("CreateSecret")]
        public async Task<IActionResult> CreateSecretAsync(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = SecretsBasePath)] HttpRequest req,
            ILogger log)
        {
            log.LogInformation("Creating new secret");

            string requestBody = await new StreamReader(req.Body).ReadToEndAsync();

            var createSecretRequest = JsonSerializer.Deserialize<CreateSecretRequest>(requestBody, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });

            if (createSecretRequest is null)
            {
                log.LogError($"Failed to deserialize request to {typeof(CreateSecretRequest)}");
                return new StatusCodeResult(500);
            }

            return await CreateSecretInternalAsync(req, createSecretRequest, log);
        }

        [FunctionName("SlackCreateSecret")]
        public async Task<IActionResult> SlackCreateSecretAsync(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = $"{SecretsBasePath}-slack")] HttpRequest req,
            [Queue("slack-create-secret")] IAsyncCollector<CreateSecretRequest> slackQueue,
            ILogger log)
        {
            log.LogInformation("Creating new secret");

            var formCollection = await req.ReadFormAsync();

            var createSecretRequest = new CreateSecretRequest
            {
                Text = formCollection["text"],
                BaseSecretsPath = $"{req.Scheme}://{req.Host}/api/v1/secrets/",
                SlackChannelId = formCollection["channel_id"],
                SlackTeamId = formCollection["team_id"]
            };

            await slackQueue.AddAsync(createSecretRequest);

            return new OkObjectResult(new
            {
                replace_original = true,
                text = "We received your request and we're working on it..."
            });

            //return await CreateSecretInternalAsync(req, createSecretRequest, log);
        }

        [FunctionName("AccessSecret")]
        public async Task<IActionResult> AccessSecretAsync(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = $"{SecretsBasePath}/{{secretId}}")] HttpRequest req,
            ExecutionContext executionContext,
            Guid secretId,
            ILogger log)
        {
            log.LogInformation($"Attempting to access secret {secretId}");

            var (secret, error) = await _snapSecretBusinessLogic.AccessSecretAsync(secretId);

            if (error != null)
            {
                return new ObjectResult(error.ToResponse())
                {
                    StatusCode = 500
                };
            }

            if (secret is null)
            {
                return new StatusCodeResult(500);
            }

            var path = Path.Combine(executionContext.FunctionDirectory, "../secret.html");
            var html = File.ReadAllText(path).Replace("{{SECRET}}", secret.Text);

            return new ContentResult
            {
                Content = html,
                ContentType = "text/html",
                StatusCode = 200
            };
        }

        private async Task<IActionResult> CreateSecretInternalAsync(
            HttpRequest req,
            CreateSecretRequest createSecretRequest,
            ILogger log)
        {
            var secret = createSecretRequest.ToShareableTextSecret();

            if (secret is null)
            {
                log.LogError($"Failed to convert {typeof(CreateSecretRequest)} request to {typeof(IShareableTextSecret)}");
                return new StatusCodeResult(500);
            }

            var (secretId, error) = await _snapSecretBusinessLogic.SubmitSecretAsync(secret);

            if (error != null)
            {
                return new ObjectResult(error.ToResponse())
                {
                    StatusCode = 500
                };
            }

            if (req.Path.ToString().Contains("-slack"))
            {
                var path = req.Path.ToString().Replace("-slack", string.Empty);

                return new OkObjectResult(new
                {
                    replace_original = true,
                    text = $"{req.Scheme}://{req.Host}{path}/{secretId}"
                });
            }
            else
            {
                return new CreatedResult($"{req.Scheme}://{req.Host}{req.Path}/{secretId}", new
                {
                    message = "Successfully created secret"
                });
            }
        }
    }

    // TODO - abstract away the Slack specific pieces
    public class CreateSecretRequest
    {
        public string? Prompt { get; set; }
        public string? Answer { get; set; }
        
        [Required]
        public string? Text { get; set; }

        public TimeSpan ExpireIn { get; set; }

        public string? BaseSecretsPath { get; set; }

        public string? SlackChannelId { get; set; }

        public string? SlackTeamId { get; set; }

        public IShareableTextSecret? ToShareableTextSecret()
        {
            if (string.IsNullOrEmpty(Text))
            {
                return default;
            }

            return new ShareableTextSecret(Text)
                .WithPrompt(Prompt, Answer)
                .WithExpireIn(ExpireIn);
        }
    }
}
