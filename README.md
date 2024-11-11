# Issue Action Approval Workflow
This project presents a solution that enables secure, one-way human approval workflows between on-premises systems and AWS cloud services.

## Issue Action Approval Lambda Function

This AWS Lambda function handles the approval workflow for issue actions, managing token validation, status updates, and notifications.

## Overview

The function processes approval requests by:
- Validating approval tokens
- Updating issue and token statuses in DynamoDB
- Revoking other active tokens
- Sending notifications via SQS and email (AWS SES)

## Prerequisites

- .NET 6.0 or later
- AWS Account with appropriate permissions
- AWS CLI configured locally for deployment
- The following AWS services configured:
  - DynamoDB tables (IssueActionApproval and IssueActionToken)
  - SQS Queue
  - SES (for email notifications)

## Required AWS Resources

### DynamoDB Tables

#### 1. IssueActionApproval Table:

Table Name: IssueActionApproval

Primary Key: RequestId (String)

Attributes:

Status (String)

ActionParameters (Map)

#### 2. IssueActionToken Table:

Table Name: IssueActionToken
Primary Key:


RequestId (String)

Token (String) Attributes:

Status (String)

TokenStatus (String)

ExpirationTime (String)

ApproverEmail (String)

### SQS Queue
- FIFO Queue for maintaining message order
- Message retention: 14 days (configurable)

### SES Configuration
- Verified sender email address
- Appropriate sending limits for your use case

## Environment Variables
ISSUE_TABLE_NAME=IssueActionApproval

TOKEN_TABLE_NAME=IssueActionToken

ISSUE_ACTION_SQS_QUEUE_URL=<your-sqs-queue-url>

SENDER_EMAIL=<your-verified-ses-email>


## Required IAM Permissions

The Lambda function requires the following permissions:

```json
{
    "Version": "2012-10-17",
    "Statement": [
        {
            "Effect": "Allow",
            "Action": [
                "dynamodb:GetItem",
                "dynamodb:UpdateItem",
                "dynamodb:Query"
            ],
            "Resource": [
                "arn:aws:dynamodb:*:*:table/IssueActionApproval",
                "arn:aws:dynamodb:*:*:table/IssueActionToken"
            ]
        },
        {
            "Effect": "Allow",
            "Action": [
                "sqs:SendMessage"
            ],
            "Resource": "arn:aws:sqs:*:*:IssueActionQueue.fifo"
        },
        {
            "Effect": "Allow",
            "Action": [
                "ses:SendEmail",
                "ses:SendRawEmail"
            ],
            "Resource": "*"
        }
    ]
}
```

### NuGet Dependencies
```
<PackageReference Include="Amazon.Lambda.Core" Version="2.1.0" />
<PackageReference Include="Amazon.Lambda.APIGatewayEvents" Version="2.6.0" />
<PackageReference Include="AWSSDK.DynamoDBv2" Version="3.7.x" />
<PackageReference Include="AWSSDK.SQS" Version="3.7.x" />
<PackageReference Include="AWSSDK.SimpleEmail" Version="3.7.x" />
```
### API Gateway Integration
#### Request Parameters

action: String (Required)

requestId: String (Required)

token: String (Required)

#### Response

Response Codes

200: Success

400: Invalid request or token

500: Internal server error