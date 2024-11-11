using Amazon.Lambda.Core;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.SQS;
using Amazon.SQS.Model;
using System;
using System.Threading.Tasks;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Amazon.SimpleEmail;
using Amazon.Runtime;
using Amazon.SimpleEmail.Model;
using static System.Collections.Specialized.BitVector32;
using Indubit.IssueApprovalWorkflow.Core.Models;

// Assembly attribute to enable the Lambda function's JSON input to be converted into a .NET class.
[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]

namespace Indubit.IssueApprovalWorkflow.Lambda;

public class IssueActionFunction
{
    private readonly IAmazonDynamoDB _dynamoDbClient;
    private readonly IAmazonSQS _sqsClient;
    private readonly IAmazonSimpleEmailService _sesClient;
    private readonly string _issueTable;
    private readonly string _tokenTable;
    private readonly string _queueUrl;

    public IssueActionFunction()
    {
        _dynamoDbClient = new AmazonDynamoDBClient();
        _sqsClient = new AmazonSQSClient();
        _sesClient = new AmazonSimpleEmailServiceClient();
        _issueTable = Environment.GetEnvironmentVariable("ISSUE_TABLE_NAME") ?? "IssueActionApproval";
        _tokenTable = Environment.GetEnvironmentVariable("TOKEN_TABLE_NAME") ?? "IssueActionToken";
        _queueUrl = Environment.GetEnvironmentVariable("ISSUE_ACTION_SQS_QUEUE_URL") ?? "";
    }

    public async Task<APIGatewayProxyResponse> FunctionHandler(APIGatewayProxyRequest request, ILambdaContext context)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(_queueUrl))
                return new APIGatewayProxyResponse
                {
                    StatusCode = 400,
                    Body = "ISSUE_ACTION_SQS_QUEUE_URL is missing"
                };

            if (request?.QueryStringParameters == null)
            {
                return new APIGatewayProxyResponse
                {
                    StatusCode = 400,
                    Body = "Missing query string parameters"
                };
            }

            var approvalAction = request.QueryStringParameters.ContainsKey("approvalAction") ? request.QueryStringParameters["approvalAction"] : null;
            var requestId = request.QueryStringParameters.ContainsKey("requestId") ? request.QueryStringParameters["requestId"] : null;
            var token = request.QueryStringParameters.ContainsKey("token") ? request.QueryStringParameters["token"] : null;

            if (string.IsNullOrEmpty(approvalAction) || string.IsNullOrEmpty(requestId) || string.IsNullOrEmpty(token))
            {
                return new APIGatewayProxyResponse
                {
                    StatusCode = 400,
                    Body = "Missing required parameters"
                };
            }

            // Validate issue record
            var issueValidation = await ValidateIssueRecord(requestId, token);
            if (issueValidation.IsError)
            {
                return issueValidation.Response;
            }

            // Validate token record
            var tokenValidation = await ValidateTokenRecord(requestId, token);
            if (tokenValidation.IsError)
            {
                return tokenValidation.Response;
            }

            // Process the action
            var actionResult = await ProcessAction(requestId, token, approvalAction, tokenValidation.ApproverEmail);
            if (actionResult.IsError)
            {
                return actionResult.Response;
            }

            // Send notification to SQS
            var sqsResult = await SendToSQS(issueValidation.ApprovalRequest, approvalAction, tokenValidation.ApproverEmail);
            if (sqsResult.IsError)
            {
                return sqsResult.Response;
            }

            await SendApprovalEmail(issueValidation.ApprovalRequest, approvalAction, tokenValidation.ApproverEmail);

            return new APIGatewayProxyResponse
            {
                StatusCode = 200,
                Body = $"Action '{issueValidation.ApprovalRequest.Action}' {approvalAction} successfully."
            };
        }
        catch (Exception ex)
        {
            context.Logger.LogError($"Error processing request: {ex}");
            return new APIGatewayProxyResponse
            {
                StatusCode = 500,
                Body = "Internal server error"
            };
        }
    }

    private async Task<(bool IsError, ApprovalRequest ApprovalRequest, APIGatewayProxyResponse Response)> ValidateIssueRecord(string requestId, string token)
    {
        var approvalRequest = new ApprovalRequest
        {
            RequestId = requestId,
        };

        var getIssueResponse = await _dynamoDbClient.GetItemAsync(new GetItemRequest
        {
            TableName = _issueTable,
            Key = new Dictionary<string, AttributeValue>
            {
                { "RequestId", new AttributeValue { S = requestId } }
            }
        });

        if (getIssueResponse.Item == null ||
            !getIssueResponse.Item.TryGetValue("Status", out var issueStatusAttribute) ||
            issueStatusAttribute.S != "Pending")
        {
            return (true, approvalRequest, new APIGatewayProxyResponse
            {
                StatusCode = 400,
                Body = "Invalid issue status or already processed or not found"
            });
        }

        getIssueResponse.Item.TryGetValue("IncidentId", out var incidentId);
        getIssueResponse.Item.TryGetValue("Action", out var action);
        getIssueResponse.Item.TryGetValue("Description", out var description);
        getIssueResponse.Item.TryGetValue("ActionParameters", out var actionParametersAttribute);
        getIssueResponse.Item.TryGetValue("ApproverEmails", out var approverEmailsAttribute);

        var approverEmails = new List<string>();
        if (approverEmailsAttribute != null && approverEmailsAttribute.SS != null)
        {
            approverEmails = approverEmailsAttribute.SS;
        }
        approvalRequest.ApproverEmails = approverEmails;

        var actionParameters = new Dictionary<string, string>();
        if (actionParametersAttribute != null && actionParametersAttribute.M != null)
        {
            actionParameters = actionParametersAttribute.M
                .ToDictionary(
                    kvp => kvp.Key,
                    kvp => kvp.Value.S
                );
        }
        approvalRequest.Action = action?.S;
        approvalRequest.IncidentId = incidentId?.S;
        approvalRequest.Description = description?.S;
        approvalRequest.ActionParameters = actionParameters;
        approvalRequest.Status = Enum.Parse<ApprovalStatus>(getIssueResponse.Item["Status"].S);

        return (false, approvalRequest, new APIGatewayProxyResponse
        {
            StatusCode = 200,
            Body = "Issue record is valid"
        });
    }

    private async Task<(bool IsError, APIGatewayProxyResponse Response, string ApproverEmail)> ValidateTokenRecord(string requestId, string token)
    {
        var getItemResponse = await _dynamoDbClient.GetItemAsync(new GetItemRequest
        {
            TableName = _tokenTable,
            Key = new Dictionary<string, AttributeValue>
            {
                { "RequestId", new AttributeValue { S = requestId } },
                { "Token", new AttributeValue { S = token } }
            }
        });

        if (!IsTokenValid(getItemResponse.Item, out var errorMessage, out var approverEmail))
        {
            return (true, new APIGatewayProxyResponse
            {
                StatusCode = 400,
                Body = errorMessage
            }, approverEmail);
        }

        return (false, new APIGatewayProxyResponse
        {
            StatusCode = 200,
            Body = "Token record is valid"
        }, approverEmail);
    }

    private bool IsTokenValid(Dictionary<string, AttributeValue> item, out string? errorMessage, out string approverEmail)
    {
        approverEmail = "";
        if (item == null)
        {
            errorMessage = "Token not found";
            return false;
        }

        if (!item.TryGetValue("Status", out var statusAttribute) ||
            !item.TryGetValue("TokenStatus", out var tokenStatusAttribute) ||
            !item.TryGetValue("ExpirationTime", out var expirationAttribute) ||
            !item.TryGetValue("ApproverEmail", out var approverEmailAttribute))
        {
            errorMessage = "Missing required attributes";
            return false;
        }

        approverEmail = approverEmailAttribute.S;

        if (statusAttribute.S != "Pending")
        {
            errorMessage = "Status is not Pending";
            return false;
        }

        if (tokenStatusAttribute.S != "Active")
        {
            errorMessage = "Token is not Active";
            return false;
        }

        if (!DateTime.TryParse(expirationAttribute.S, out var expirationTime) ||
            DateTime.UtcNow > expirationTime)
        {
            errorMessage = "Token has expired";
            return false;
        }

        errorMessage = null;
        return true;
    }

    private async Task<(bool IsError, APIGatewayProxyResponse Response)> ProcessAction(string requestId, string token, string action, string approverEmail)
    {
        try
        {
            await UpdateIssueRecord(requestId, action);
            await UpdateTokenRecord(requestId, token, action);
            await RevokeOtherTokens(requestId, token);

            return (false, new APIGatewayProxyResponse
            {
                StatusCode = 200,
                Body = "Action processed successfully"
            });
        }
        catch (ConditionalCheckFailedException)
        {
            return (true, new APIGatewayProxyResponse
            {
                StatusCode = 400,
                Body = "Cannot update: Token validation failed"
            });
        }
        catch (Exception ex)
        {
            return (true, new APIGatewayProxyResponse
            {
                StatusCode = 500,
                Body = "Update Failed." + ex.ToString()
            });
        }
    }

    private async Task<(bool IsError, APIGatewayProxyResponse Response)> SendToSQS(ApprovalRequest approvalRequest,string approvalAction,  string approverEmail)
    {
        // Send message to SQS
        // After updating the DynamoDB record


        // Prepare the message for SQS
        var message = new
        {
            approvalRequest.RequestId,
            ApprovalAction = approvalAction,
            approvalRequest.Action,
            approvalRequest.IncidentId,
            ApproverEmail = approverEmail,
            ApprovedAt = DateTime.UtcNow.ToString("o"),
            Comment = "Anything else",
            approvalRequest.ActionParameters
        };

        // Serialize the message to JSON
        string messageBody = JsonConvert.SerializeObject(message);

        // Create the send message request
        var sendMessageRequest = new SendMessageRequest
        {
            QueueUrl = _queueUrl,
            MessageBody = messageBody,
            MessageGroupId = approvalRequest.RequestId,
            //MessageDeduplicationId = Guid.NewGuid().ToString()
        };

        try
        {
            // Send the message to SQS
            var sendMessageResponse = await _sqsClient.SendMessageAsync(sendMessageRequest);
            Console.WriteLine($"Message sent to SQS. MessageId: {sendMessageResponse.MessageId}");

            return (false, new APIGatewayProxyResponse
            {
                StatusCode = 200,
                Body = "Sent to SQS successfully"
            });
        }
        catch (AmazonSQSException e)
        {
            // This catches errors specific to Amazon SQS
            return (true, new APIGatewayProxyResponse
            {
                StatusCode = 500,
                Body = $"SendToSQS Failed. AmazonSQSException. Message - {e.Message}, ErrorCode - {e.ErrorCode}" +
                $", ErrorType - {e.ErrorType}, RequestId - {e.RequestId}, StatusCode - {e.StatusCode}"
            });
        }
        catch (Exception ex)
        {
            return (true, new APIGatewayProxyResponse
            {
                StatusCode = 500,
                Body = "SendToSQS Failed." + ex.ToString()
            });
        }

    }

    private async Task UpdateIssueRecord(string requestId, string action)
    {
        await _dynamoDbClient.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = _issueTable,
            Key = new Dictionary<string, AttributeValue>
            {
                { "RequestId", new AttributeValue { S = requestId } }
            },
            UpdateExpression = "SET #status = :status",
            ExpressionAttributeNames = new Dictionary<string, string>
            {
                { "#status", "Status" }
            },
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                { ":status", new AttributeValue { S = action } }
            },
            ConditionExpression = "attribute_exists(RequestId)"
        });
    }

    private async Task UpdateTokenRecord(string requestId, string token, string action)
    {
        await _dynamoDbClient.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = _tokenTable,
            Key = new Dictionary<string, AttributeValue>
            {
                { "RequestId", new AttributeValue { S = requestId } },
                { "Token", new AttributeValue { S = token } }
            },
            UpdateExpression = "SET #status = :newStatus, #tokenStatus = :usedStatus",
            ConditionExpression = "#status = :pendingStatus AND #tokenStatus = :activeStatus AND #expirationTime > :currentTime",
            ExpressionAttributeNames = new Dictionary<string, string>
            {
                {"#status", "Status"},
                {"#tokenStatus", "TokenStatus"},
                {"#expirationTime", "ExpirationTime"}
            },
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                {":newStatus", new AttributeValue { S = action }},
                {":usedStatus", new AttributeValue { S = "Used" }},
                {":pendingStatus", new AttributeValue { S = "Pending" }},
                {":activeStatus", new AttributeValue { S = "Active" }},
                {":currentTime", new AttributeValue { S = DateTime.UtcNow.ToString("o") }}
            }
        });
    }

    private async Task RevokeOtherTokens(string requestId, string currentToken)
    {
        // Query all tokens for the RequestId where TokenStatus is active
        var queryResponse = await _dynamoDbClient.QueryAsync(new QueryRequest
        {
            TableName = _tokenTable,
            KeyConditionExpression = "RequestId = :requestId",
            FilterExpression = "#tokenStatus = :activeStatus",
            ExpressionAttributeNames = new Dictionary<string, string>
        {
            {"#tokenStatus", "TokenStatus"}
        },
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
        {
            {":requestId", new AttributeValue { S = requestId }},
            {":activeStatus", new AttributeValue { S = "Active" }}
        }
        });

        // Process each token except the current one
        foreach (var item in queryResponse.Items)
        {
            var otherToken = item["Token"].S;

            // Skip the current token
            if (otherToken == currentToken)
            {
                continue;
            }

            try
            {
                await _dynamoDbClient.UpdateItemAsync(new UpdateItemRequest
                {
                    TableName = _tokenTable,
                    Key = new Dictionary<string, AttributeValue>
                {
                    { "RequestId", new AttributeValue { S = requestId } },
                    { "Token", new AttributeValue { S = otherToken } }
                },
                    UpdateExpression = "SET #tokenStatus = :revokedStatus",
                    ConditionExpression = "#tokenStatus = :activeStatus",
                    ExpressionAttributeNames = new Dictionary<string, string>
                {
                    {"#tokenStatus", "TokenStatus"}
                },
                    ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                {
                    {":revokedStatus", new AttributeValue { S = "Revoked" }},
                    {":activeStatus", new AttributeValue { S = "Active" }}
                }
                });
            }
            catch (ConditionalCheckFailedException)
            {
                // Token is no longer active, skip it
                continue;
            }
        }
    }


    private async Task SendApprovalEmail(ApprovalRequest approvalRequest, string approvalAction, string approverEmail)
    {

        var emailBody = $@"
                {approverEmail} has {approvalAction} for Incident - {approvalRequest.IncidentId}
                
                RequestId: {approvalRequest.RequestId}
                Description: {approvalRequest.Description}
                Action: {approvalRequest.Action}
                Parameters: {string.Join(", ", approvalRequest.ActionParameters.Select(kv => $"{kv.Key}={kv.Value}"))}


            ";

        var sendRequest = new SendEmailRequest
        {
            Source = "noreply@appwithaws.com", // Replace with verified SES email
            Destination = new Destination { ToAddresses = approvalRequest.ApproverEmails},
            Message = new Amazon.SimpleEmail.Model.Message
            {
                Subject = new Content($"Approval Request: {approvalAction}"),
                Body = new Body { Text = new Content(emailBody) }
            }
        };

        try
        {
            var response = await _sesClient.SendEmailAsync(sendRequest);
            // If successful, you can log or process the response here
            Console.WriteLine($"Email sent successfully. Message ID: {response.MessageId}");
        }
        catch (AmazonSimpleEmailServiceException e)
        {
            // This catches errors specific to Amazon SES
            Console.WriteLine($"Amazon SES error: {e.Message}");
            Console.WriteLine($"Error Code: {e.ErrorCode}");
            Console.WriteLine($"Error Type: {e.ErrorType}");
            Console.WriteLine($"Request ID: {e.RequestId}");
            Console.WriteLine($"HTTP Status Code: {e.StatusCode}");

            // Handle specific SES errors
            switch (e.ErrorCode)
            {
                case "MessageRejected":
                    Console.WriteLine("The message was rejected by Amazon SES.");
                    break;
                case "MailFromDomainNotVerified":
                    Console.WriteLine("The 'From' domain is not verified with Amazon SES.");
                    break;
                case "Daily message quota exceeded":
                    Console.WriteLine("The daily message quota has been exceeded.");
                    break;
                // Add more specific error cases as needed
                default:
                    Console.WriteLine("An unknown SES error occurred.");
                    break;
            }

            // Depending on your application's needs, you might want to throw the exception
            // or return a specific error response
            throw;
        }
        catch (AmazonServiceException e)
        {
            // This catches general AWS service errors
            Console.WriteLine($"AWS service error: {e.Message}");
            Console.WriteLine($"Error Code: {e.ErrorCode}");
            Console.WriteLine($"HTTP Status Code: {e.StatusCode}");
            throw;
        }
        catch (Exception e)
        {
            // This catches any other unexpected errors
            Console.WriteLine($"Unexpected error when sending email: {e.Message}");
            throw;
        }

    }

}
