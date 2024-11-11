using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.SQS;
using Amazon.SQS.Model;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Text.Json;

namespace Indubit.IssueApprovalWorkflow.ActionProcessor
{
    public class ActionProcessorService : BackgroundService
    {
        private readonly ILogger<ActionProcessorService> _logger;
        private readonly IAmazonSQS _sqsClient;
        private readonly IAmazonDynamoDB _dynamoDb;
        private readonly WorkerOptions _options;

        /// <summary>
        /// Initializes a new instance of the <see cref="ActionProcessorService"/> class.
        /// This service is responsible for processing approval messages from an Amazon SQS queue and updating the corresponding records in DynamoDB.
        /// </summary>
        /// <param name="logger">An instance of <see cref="ILogger{ActionProcessorService}"/> for logging.</param>
        /// <param name="sqsClient">An instance of <see cref="IAmazonSQS"/> for interacting with Amazon SQS.</param>
        /// <param name="dynamoDb">An instance of <see cref="IAmazonDynamoDB"/> for interacting with Amazon DynamoDB.</param>
        /// <param name="options">An instance of <see cref="IOptions{WorkerOptions}"/> containing configuration settings for the worker service.</param>
        public ActionProcessorService(
            ILogger<ActionProcessorService> logger,
            IAmazonSQS sqsClient,
            IAmazonDynamoDB dynamoDb,
            IOptions<WorkerOptions> options)
        {
            _logger = logger;
            _sqsClient = sqsClient;
            _dynamoDb = dynamoDb;
            _options = options.Value;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("Approval Worker Service is starting...");

            try
            {
                while (!stoppingToken.IsCancellationRequested)
                {
                    await ProcessMessagesAsync(stoppingToken);
                    await Task.Delay(TimeSpan.FromSeconds(_options.PollingIntervalSeconds), stoppingToken);
                }
            }
            catch (TaskCanceledException)
            {
                _logger.LogInformation("Worker service is stopping...");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Fatal error in worker service");
                throw;
            }
        }

        private async Task ProcessMessagesAsync(CancellationToken stoppingToken)
        {
            try
            {
                var receiveRequest = new ReceiveMessageRequest
                {
                    QueueUrl = _options.QueueUrl,
                    MaxNumberOfMessages = _options.MaxNumberOfMessages,
                    WaitTimeSeconds = _options.WaitTimeSeconds,
                    VisibilityTimeout = _options.VisibilityTimeout,
                    MessageSystemAttributeNames = new List<string> { "All" }
                };

                var response = await _sqsClient.ReceiveMessageAsync(receiveRequest, stoppingToken);

                foreach (var message in response.Messages)
                {
                    try
                    {
                        await ProcessSingleMessageAsync(message, stoppingToken);
                        await DeleteMessageAsync(message.ReceiptHandle, stoppingToken);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error processing message {MessageId}", message.MessageId);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in message polling loop");
            }
        }

        private async Task ProcessSingleMessageAsync(Message message, CancellationToken stoppingToken)
        {
            var approvalMessage = JsonSerializer.Deserialize<ApprovalMessage>(message.Body)
                ?? throw new InvalidOperationException("Invalid message format");

            _logger.LogInformation("Processing approval for RequestId: {RequestId}", approvalMessage.RequestId);

            try
            {
                // TODO: Implement your custom action handling here
                var actionResult = "Action executed successfully"; // Replace with actual action execution

                // Update DynamoDB with action result
                await UpdateApprovalStatusAsync(approvalMessage, actionResult, stoppingToken);

                _logger.LogInformation("Successfully processed RequestId: {RequestId}", approvalMessage.RequestId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing RequestId: {RequestId}", approvalMessage.RequestId);
                throw;
            }
        }

        private async Task UpdateApprovalStatusAsync(ApprovalMessage message, string actionResult, CancellationToken stoppingToken)
        {
            var updateRequest = new UpdateItemRequest
            {
                TableName = _options.ApprovalTableName,
                Key = new Dictionary<string, AttributeValue>
            {
                { "RequestId", new AttributeValue { S = message.RequestId } }
            },
                UpdateExpression = "SET #status = :status, ActionResult = :result, UpdatedAt = :updateTime",
                ExpressionAttributeNames = new Dictionary<string, string>
            {
                { "#status", "Status" }
            },
                ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                { ":status", new AttributeValue { S = "COMPLETED" } },
                { ":result", new AttributeValue { S = actionResult } },
                { ":updateTime", new AttributeValue { S = DateTime.UtcNow.ToString("o") } }
            }
            };

            await _dynamoDb.UpdateItemAsync(updateRequest, stoppingToken);
        }

        private async Task DeleteMessageAsync(string receiptHandle, CancellationToken stoppingToken)
        {
            var deleteRequest = new DeleteMessageRequest
            {
                QueueUrl = _options.QueueUrl,
                ReceiptHandle = receiptHandle
            };

            await _sqsClient.DeleteMessageAsync(deleteRequest, stoppingToken);
        }
    }

}
