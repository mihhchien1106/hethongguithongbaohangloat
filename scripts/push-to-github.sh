#!/usr/bin/env bash
set -e
REPO_URL="https://github.com/mihhchien1106/hethongguithongbaohangloat.git"

echo "=== BẮT ĐẦU KHỞI TẠO GIT REPOSITORY VỚI LỊCH SỬ COMMIT ==="
rm -rf .git
git init
git branch -M main

git add .gitignore BulkNotificationSystem.sln .env.example src/Shared.Contracts/
git commit -m "feat(shared): initialize .NET 10 solution, entities, DTOs, and PostgreSQL DbContext"

git add src/Campaign.Api/
git commit -m "feat(api): implement ASP.NET Core Minimal API for campaigns and RabbitMQ batch publisher"

git add src/Notification.Worker/
git commit -m "feat(worker): add background notification worker with QoS fair dispatch, idempotency, and DLQ"

git add src/Admin.Client/
git commit -m "feat(client): build interactive Console Admin Client to manage campaigns and retry failed jobs"

git add docker-compose.yml README.md docs/ scripts/
git commit -m "docs(devops): add docker-compose orchestration, deployment guide, and project report"

git remote add origin "$REPO_URL"
git push -u origin main --force
echo "=== HOÀN TẤT ĐẨY CODE LÊN GITHUB THÀNH CÔNG! ==="
