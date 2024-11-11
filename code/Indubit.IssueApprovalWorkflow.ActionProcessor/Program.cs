using Indubit.IssueApprovalWorkflow.ActionProcessor;

using Amazon.DynamoDBv2;
using Amazon.SQS;
using Microsoft.Extensions.Configuration;   


var builder = Host.CreateDefaultBuilder(args);

builder.ConfigureServices((context, services) =>
{
    // Configure options
    services.Configure<WorkerOptions>(context.Configuration.GetSection("WorkerOptions"));

    // Configure AWS services
    services.AddAWSService<IAmazonSQS>();
    services.AddAWSService<IAmazonDynamoDB>();

    // Add worker service
    services.AddHostedService<ActionProcessorService>();
});

var host = builder.Build();
await host.RunAsync();


