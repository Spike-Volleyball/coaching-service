#!/bin/bash
# Create bin/Debug subdirs in anonymous volumes to prevent .NET SDK glob failures
for dir in Coaching Coaching.Application Coaching.Domain Coaching.Infrastructure Coaching.Tests.Integration Coaching.Tests.Unit \
    shared/Shared shared/Shared.Messaging shared/Shared.Messaging.Contracts \
    shared/Shared.Contracts shared/Shared.DataAccess shared/Shared.Logging \
    shared/Shared.Microservices shared/Shared.Services shared/Shared.Testing shared/Shared.Tests; do
    mkdir -p "/app/$dir/bin/Debug/net10.0" "/app/$dir/obj"
done

exec dotnet watch run --project Coaching/Coaching.csproj --no-launch-profile --non-interactive
