# Script tự động khởi tạo Git với lịch sử nhiều commit chuẩn yêu cầu môn học và đẩy lên GitHub
$ErrorActionPreference = "Stop"
$RepoUrl = "https://github.com/mihhchien1106/hethongguithongbaohangloat.git"

Write-Host "=== BẮT ĐẦU KHỞI TẠO GIT REPOSITORY VỚI LỊCH SỬ COMMIT ===" -ForegroundColor Cyan

if (Test-Path ".git") {
    Remove-Item -Recurse -Force ".git"
}

git init
git branch -M main

# Commit 1: Khởi tạo cấu trúc Solution và Shared Contracts
git add .gitignore BulkNotificationSystem.sln .env.example src/Shared.Contracts/
git commit -m "feat(shared): initialize .NET 10 solution, entities, DTOs, and PostgreSQL DbContext"

# Commit 2: Xây dựng Campaign Minimal API & RabbitMQ Publisher
git add src/Campaign.Api/
git commit -m "feat(api): implement ASP.NET Core Minimal API for campaigns and RabbitMQ batch publisher"

# Commit 3: Xây dựng Notification Worker xử lý bất đồng bộ
git add src/Notification.Worker/
git commit -m "feat(worker): add background notification worker with QoS fair dispatch, idempotency, and DLQ"

# Commit 4: Xây dựng Admin Console Client
git add src/Admin.Client/
git commit -m "feat(client): build interactive Console Admin Client to manage campaigns and retry failed jobs"

# Commit 5: Cấu hình Docker Compose & Báo cáo tài liệu
git add docker-compose.yml README.md docs/ scripts/
git commit -m "docs(devops): add docker-compose orchestration, deployment guide, and project report"

git remote add origin $RepoUrl
Write-Host "Đang đẩy mã nguồn lên $RepoUrl ..." -ForegroundColor Yellow
git push -u origin main --force

Write-Host "=== HOÀN TẤT ĐẨY CODE LÊN GITHUB THÀNH CÔNG! ===" -ForegroundColor Green
