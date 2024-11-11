using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.SimpleEmail;
using Amazon.SimpleEmail.Model;
using Microsoft.AspNetCore.Mvc;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Indubit.IssueApprovalWorkflow.Core.Models;
using Amazon.Runtime.Internal;
using Newtonsoft.Json.Linq;
using Amazon.Runtime;
using Amazon;

namespace Indubit.IssueApprovalWorkflow.Trigger.Api.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class ApprovalController : ControllerBase
    {
        private readonly IAmazonDynamoDB _dynamoDbClient;
        private readonly IAmazonSimpleEmailService _sesClient;
        private readonly string _lambdaFunctionUrl; // URL of your Lambda function

        public ApprovalController(IAmazonDynamoDB dynamoDbClient, IAmazonSimpleEmailService sesClient, IConfiguration configuration)
        {
            _dynamoDbClient = dynamoDbClient;
            _sesClient = sesClient;
            _lambdaFunctionUrl = "";
        }

        [HttpPost]
        public async Task<IActionResult> CreateApprovalRequest([FromBody] ApprovalRequest request)
        {
            if (!ModelState.IsValid)
            {
                return BadRequest(ModelState);
            }

            try
            {
                // Insert the main request into issue_approval table
                await InsertIntoIssueApprovalTable(request);

                // Process each approver
                foreach (var email in request.ApproverEmails)
                {
                    var token = GenerateUniqueToken();
                    await InsertIntoApprovalTokenTable(request.RequestId, email, token);
                    await SendApprovalEmail(request, email, token);
                }

                return Ok("Approval request processed successfully");
            }
            catch (Exception ex)
            {
                // Log the exception
                return StatusCode(500, "An error occurred while processing the request");
            }
        }

        private async Task InsertIntoIssueApprovalTable(ApprovalRequest request)
        {
            // 1a. Create DynamoDB record with Pending status
            var item = new Dictionary<string, AttributeValue>
            {
                ["RequestId"] = new AttributeValue { S = request.RequestId },
                ["IncidentId"] = new AttributeValue { S = request.IncidentId },
                ["Description"] = new AttributeValue { S = request.Description },
                ["Action"] = new AttributeValue { S = request.Action },
                ["ActionParameters"] = new AttributeValue { M = request.ActionParameters.ToDictionary(kvp => kvp.Key, kvp => new AttributeValue { S = kvp.Value }) },
                ["Status"] = new AttributeValue { S = ApprovalStatus.Pending.ToString() },
                ["CreatedAt"] = new AttributeValue { S = DateTime.UtcNow.ToString("O") },
                ["ApproverEmails"] = new AttributeValue { SS = request.ApproverEmails },
                ["ExpiryTime"] = new AttributeValue
                {
                    N = ((DateTimeOffset)DateTime.UtcNow.AddDays(7)).ToUnixTimeSeconds().ToString()
                }
            };
            //TBD: Set required expiration



            var putItemRequest = new PutItemRequest
            {
                TableName = "IssueActionApproval",
                Item = item
            };

            try
            {
                var response = await _dynamoDbClient.PutItemAsync(putItemRequest);
                // If successful, you can log or process the response here
            }
            catch (AmazonDynamoDBException e)
            {
                // This catches errors specific to DynamoDB
                Console.WriteLine($"DynamoDB specific error: {e.Message}");
                if (e.ErrorCode != null)
                {
                    Console.WriteLine($"Error Code: {e.ErrorCode}");
                }
                if (e.StatusCode != System.Net.HttpStatusCode.OK)
                {
                    Console.WriteLine($"HTTP Status Code: {e.StatusCode}");
                }
                // Handle the error appropriately (e.g., return a specific error response)
                throw; 
            }
            catch (AmazonServiceException e)
            {
                // This catches general AWS service errors
                Console.WriteLine($"AWS service error: {e.Message}");
                Console.WriteLine($"Error Code: {e.ErrorCode}");
                Console.WriteLine($"HTTP Status Code: {e.StatusCode}");
                // Handle the error appropriately
                throw;
            }
            catch (Exception e)
            {
                // This catches any other unexpected errors
                Console.WriteLine($"Unexpected error: {e.Message}");
                // Handle the error as needed
                throw;
            }

        }

        private async Task InsertIntoApprovalTokenTable(string requestId, string email, string token)
        {
            var item = new Dictionary<string, AttributeValue>
        {
            { "Token", new AttributeValue { S = token } },
            { "RequestId", new AttributeValue { S = requestId } },
            { "ApproverEmail", new AttributeValue { S = email } },
            { "Status", new AttributeValue { S = "Pending" } },
            { "TokenStatus", new AttributeValue { S = "Active" } },
            { "ExpirationTime", new AttributeValue { S = DateTime.UtcNow.AddDays(1).ToString("O") } }
            };
            //TBD: Set required expiration

            var putItemRequest = new PutItemRequest
            {
                TableName = "IssueActionToken",
                Item = item
            };

            await _dynamoDbClient.PutItemAsync(putItemRequest);
        }
        private async Task SendApprovalEmail(ApprovalRequest request, string email, string token)
        {
            var approvalLink = $"{_lambdaFunctionUrl}?approvalAction=Approved&token={token}&requestId={request.RequestId}";
            var rejectionLink = $"{_lambdaFunctionUrl}?approvalAction=Rejected&token={token}&requestId={request.RequestId}";
            var silentLink = $"{_lambdaFunctionUrl}?approvalAction=Silent&token={token}&requestId={request.RequestId}";

            var emailBody = $@"
                Approval Required for Incident {request.IncidentId}
                
                Description: {request.Description}
                Proposed Action: {request.Action}
                Parameters: {string.Join(", ", request.ActionParameters.Select(kv => $"{kv.Key}={kv.Value}"))}
                

            To approve or reject this request, please click on the following links:

            To approve, click here: {approvalLink}
            To reject, click here: {rejectionLink}
            To silently ignore, click here: {silentLink}
            
            This request will expire in 24 HOURS.";

            var sendRequest = new SendEmailRequest
            {
                Source = "", //TBD: Replace with verified SES email 
                Destination = new Destination { ToAddresses = new List<string> { email } },
                Message = new Message
                {
                    Subject = new Content($"Approval Request: {request.RequestId}"),
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

        private string GenerateUniqueToken()
        {
            return Guid.NewGuid().ToString();
        }
    }
}
