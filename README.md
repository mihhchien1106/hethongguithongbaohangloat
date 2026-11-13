# Đề tài 11: Hệ thống gửi thông báo hàng loạt (Bulk Notification System)

Hệ thống phân tán cho phép tạo chiến dịch thông báo (Email/SMS/Push) đến hàng loạt người nhận, sử dụng **ASP.NET Core Minimal API (.NET 10)**, **RabbitMQ**, **PostgreSQL**, và nhiều **Notification Workers** xử lý bất đồng bộ độc lập.

---

## 1. Kiến trúc hệ thống

```text
+----------------+       REST HTTP        +------------------+
|  Admin Client  | ---------------------> |   Campaign API   |
| (Console App)  |                        |  (Minimal API)   |
+----------------+                        +--------+---------+
                                                   |
                        +--------------------------+--------------------------+
                        | (1) Lưu Campaign & Jobs                             | (2) Publish Batch Jobs
                        v                                                     v
              +-------------------+                                 +-------------------+
              |    PostgreSQL     |                                 |     RabbitMQ      |
              | (notification_db) |                                 |  Message Broker   |
              +---------+---------+                                 +---------+---------+
                        ^                                                     |
                        | (4) Cập nhật Sent / Failed                          | (3) Fair Dispatch (QoS=1)
                        |     (Idempotent Check)                              v
                        |                                           +-------------------+
                        +------------------------------------------ |   Notification    |
                        |                                           |   Worker Node 1   |
                        |                                           +-------------------+
                        |                                           +-------------------+
                        +------------------------------------------ |   Notification    |
                                                                    |   Worker Node 2   |
                                                                    +-------------------+
```

### Các tính năng chính & Mở rộng đã hoàn thiện:
1. **Tạo chiến dịch & Danh sách người nhận**: Loại bỏ trùng lặp địa chỉ tự động, khởi tạo trạng thái `Draft` và các job ở trạng thái `Pending`.
2. **Xử lý bất đồng bộ qua RabbitMQ**: `POST /campaigns/{id}/start` đẩy hàng loạt job vào Exchange `notification.exchange` (Persistent Message).
3. **Multi-Worker chạy độc lập**: Khởi chạy mặc định 2 container Worker (`Worker-Node-01`, `Worker-Node-02`) cấu hình `BasicQos(prefetchCount: 1)` để chia tải công bằng (Fair Dispatch).
4. **Theo dõi tiến độ thời gian thực**: Thống kê chính xác số lượng `Pending`, `Processing`, `Sent`, `Failed` và tên Worker đã xử lý từng thông báo.
5. **Chạy lại các job thất bại (Retry Failed Jobs)**: API `POST /campaigns/{id}/retry-failed` gom toàn bộ các thông báo `Failed`, đặt lại trạng thái và đẩy vào RabbitMQ kèm số lần thử (`AttemptNumber`).
6. **Các yêu cầu mở rộng đã tích hợp**:
   - **Dead-Letter Exchange / Queue (DLX/DLQ)**: Tự động định tuyến bản tin lỗi nghiêm trọng sang `notification.dlq.queue`.
   - **Idempotent Consumer**: Worker kiểm tra trạng thái job trong DB trước khi gửi; nếu đã `Sent` thì bỏ qua để chống gửi trùng.
   - **Correlation ID**: Gắn mã định danh xuyên suốt từ lúc gọi API kích hoạt chiến dịch đến log của từng Worker.
   - **Health Check & Auto-Reconnect**: Tích hợp `/health` cho API, `healthcheck` cho PostgreSQL/RabbitMQ trong Docker và cơ chế tự động kết nối lại khi Message Broker hoặc DB tạm dừng.

---

## 2. Cấu trúc thư mục

```text
hethongguithongbaohangloat/
├── BulkNotificationSystem.sln
├── docker-compose.yml
├── .env.example
├── README.md
├── docs/
│   └── BAO-CAO-BAI-TAP-LON.md          # Nội dung chi tiết để đưa vào quyển báo cáo Word/PDF
├── scripts/
│   ├── push-to-github.ps1              # Script tự động tạo lịch sử commit và push lên GitHub (Windows)
│   └── push-to-github.sh               # Script tự động tạo lịch sử commit và push lên GitHub (Linux/macOS)
└── src/
    ├── Shared.Contracts/               # Entities, DTOs, RabbitMQ Messages, EF Core AppDbContext
    ├── Campaign.Api/                   # ASP.NET Core Minimal API (.NET 10) + Dockerfile
    ├── Notification.Worker/            # Background Service Worker (.NET 10) + Dockerfile
    └── Admin.Client/                   # Console App quản trị chiến dịch (.NET 10) + Dockerfile
```

---

## 3. Hướng dẫn cài đặt và khởi chạy bằng Docker Compose (Khuyên dùng khi chấm thi)

### Yêu cầu:
- Đã cài đặt **Docker Desktop** (hoặc Docker Engine + Docker Compose).

### Bước 1: Khởi chạy toàn bộ hệ thống (Database, RabbitMQ, API, 2 Workers)
Mở terminal tại thư mục gốc của project và chạy:

```bash
docker compose up -d --build postgres rabbitmq campaign-api notification-worker-1 notification-worker-2
```

Kiểm tra trạng thái các container:
```bash
docker compose ps
```
- **Campaign API**: `http://localhost:5080` (Health check: `http://localhost:5080/health`)
- **RabbitMQ Management UI**: `http://localhost:15672` (Tài khoản: `guest` / `guest`)
- **PostgreSQL**: `localhost:5432` (Database: `notification_db`, User/Pass: `postgres`/`postgres`)

### Bước 2: Chạy Admin Console Client để tương tác
Bạn có thể chạy Admin Client trực tiếp trong Docker network:
```bash
docker compose run --rm admin-client
```
Hoặc nếu máy có cài .NET SDK, chạy trực tiếp trên máy host:
```bash
dotnet run --project src/Admin.Client/Admin.Client.csproj
```

### Bước 3: Theo dõi log xử lý song song của 2 Worker
Mở một cửa sổ terminal khác và chạy:
```bash
docker compose logs -f notification-worker-1 notification-worker-2 campaign-api
```

---

## 4. Danh sách API Endpoints

| Phương thức | Endpoint | Mô tả |
| :--- | :--- | :--- |
| `POST` | `/campaigns` | Tạo chiến dịch thông báo mới kèm danh sách người nhận |
| `GET` | `/campaigns` | Lấy danh sách toàn bộ chiến dịch kèm số lượng `Pending`, `Sent`, `Failed` |
| `GET` | `/campaigns/{id}` | Xem chi tiết chiến dịch và thống kê trạng thái |
| `POST` | `/campaigns/{id}/start` | Sinh các job từ danh sách `Pending` và đẩy vào RabbitMQ |
| `GET` | `/campaigns/{id}/deliveries` | Tra cứu chi tiết kết quả gửi của từng người nhận (hỗ trợ `?status=Failed`) |
| `POST` | `/campaigns/{id}/retry-failed` | Chạy lại toàn bộ các job có trạng thái `Failed` của chiến dịch |
| `GET` | `/health` | Kiểm tra trạng thái hoạt động của Campaign API |

### Ví dụ gọi API bằng cURL:

**1. Tạo chiến dịch:**
```bash
curl -X POST http://localhost:5080/campaigns \
  -H "Content-Type: application/json" \
  -d '{
    "name": "Khuyến mãi Black Friday",
    "subject": "Giảm giá 50% toàn bộ sản phẩm",
    "content": "Nhập mã BF50 ngay hôm nay!",
    "channel": "Email",
    "recipients": [
      { "address": "khachhang1@example.com", "name": "Nguyễn Văn A" },
      { "address": "khachhang2.fail@example.com", "name": "Trần Thị B" },
      { "address": "khachhang3@example.com", "name": "Lê Văn C" }
    ]
  }'
```

**2. Bắt đầu gửi chiến dịch:**
```bash
curl -X POST http://localhost:5080/campaigns/{id}/start
```

**3. Xem chi tiết kết quả gửi:**
```bash
curl http://localhost:5080/campaigns/{id}/deliveries
```

**4. Chạy lại các thông báo bị lỗi:**
```bash
curl -X POST http://localhost:5080/campaigns/{id}/retry-failed
```
