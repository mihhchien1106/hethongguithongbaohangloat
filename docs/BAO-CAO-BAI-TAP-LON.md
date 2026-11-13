# BÁO CÁO BÀI TẬP LỚN HỌC PHẦN PHÁT TRIỂN ỨNG DỤNG PHÂN TÁN
**Đề tài 11: Hệ thống gửi thông báo hàng loạt (Bulk Notification System)**

---

## CHƯƠNG 1: MÔ TẢ BÀI TOÁN VÀ PHÂN TÍCH YÊU CẦU

### 1.1. Đặt vấn đề
Trong các hệ thống phần mềm hiện đại (Thương mại điện tử, Ngân hàng số, Quản trị doanh nghiệp), nhu cầu gửi thông báo hàng loạt (Email, SMS, Push Notification) tới hàng nghìn hoặc hàng triệu người dùng diễn ra thường xuyên. Nếu xử lý đồng bộ (Synchronous) ngay trong HTTP Request của API:
- Thời gian phản hồi (Latency) cực kỳ cao, dễ gây lỗi `504 Gateway Timeout`.
- Khi một cổng gửi thông báo bên thứ ba chậm hoặc lỗi, toàn bộ luồng xử lý bị nghẽn.
- Không thể mở rộng (Scale-out) độc lập tầng xử lý gửi tin.

Vì vậy, kiến trúc hướng sự kiện / hàng đợi tin nhắn (**Message-Driven Architecture**) sử dụng **RabbitMQ** kết hợp các **Background Worker** bất đồng bộ là giải pháp tối ưu.

### 1.2. Các yêu cầu chức năng
1. **Quản lý chiến dịch**: Cho phép tạo chiến dịch với tiêu đề, nội dung, kênh gửi và danh sách người nhận.
2. **Phân rã Job và đẩy vào Message Broker**: Khi kích hoạt chiến dịch (`/start`), API chuyển đổi mỗi người nhận thành một message độc lập và đẩy vào RabbitMQ.
3. **Xử lý phân tán đa Worker**: Nhiều tiến trình `Notification.Worker` cùng lắng nghe hàng đợi, tự động chia tải và xử lý độc lập.
4. **Theo dõi và thống kê**: Ghi nhận trạng thái từng thông báo (`Pending`, `Processing`, `Sent`, `Failed`) và tổng hợp tiến độ theo chiến dịch.
5. **Cơ chế chịu lỗi & Retry**: Lưu vết nguyên nhân lỗi, số lần thử lại (`RetryCount`) và cho phép kích hoạt chạy lại các job thất bại (`/retry-failed`).

---

## CHƯƠNG 2: KIẾN TRÚC HỆ THỐNG VÀ THIẾT KẾ THÀNH PHẦN

### 2.1. Sơ đồ kiến trúc tổng thể
```text
Admin Client (Console) ---> Campaign API (Minimal API) ---> RabbitMQ (Exchange/Queue/DLQ)
                                    |                                   |
                                    v                                   v
                            PostgreSQL Database <------ Notification Workers (Node 1, Node 2...)
```

### 2.2. Thiết kế các thành phần
1. **`Shared.Contracts`**: Thư viện chứa các thực thể cơ sở dữ liệu (`CampaignEntity`, `NotificationJobEntity`), `AppDbContext` (Entity Framework Core với PostgreSQL) và cấu trúc bản tin `NotificationDispatchMessage`.
2. **`Campaign.Api` (ASP.NET Core Minimal API - .NET 10)**:
   - Tiếp nhận yêu cầu HTTP từ Client.
   - Lưu thông tin chiến dịch vào PostgreSQL.
   - Sử dụng `IBasicPublishBatch` của RabbitMQ để đẩy hàng loạt job với hiệu năng cao và thuộc tính `Persistent = true`.
3. **`Notification.Worker` (.NET 10 Worker Service)**:
   - Sử dụng `AsyncEventingBasicConsumer` kết hợp `BasicQos(prefetchCount: 1)` để đảm bảo **Fair Dispatch** (Worker nào rảnh mới nhận thêm job).
   - Kiểm tra tính **Idempotent** (chống gửi lặp nếu job đã `Sent`).
   - Ghi nhận tên Worker xử lý (`ProcessedByWorker`) và cập nhật trạng thái vào PostgreSQL.
4. **`Admin.Client` (.NET 10 Console App)**: Giao diện dòng lệnh trực quan giúp quản trị viên thực hiện toàn bộ kịch bản nghiệp vụ.

---

## CHƯƠNG 3: CƠ CHẾ GIAO TIẾP PHÂN TÁN VÀ KHẢ NĂNG CHỊU LỖI

1. **Giao tiếp đồng bộ (RESTful HTTP)**: Giữa `Admin.Client` và `Campaign.Api`.
2. **Giao tiếp bất đồng bộ (AMQP 0-9-1 qua RabbitMQ)**:
   - **Exchange**: `notification.exchange` (kiểu `Direct`, `durable: true`).
   - **Main Queue**: `notification.dispatch.queue` (`durable: true`).
   - **Dead-Letter Exchange/Queue**: `notification.dlx.exchange` -> `notification.dlq.queue` để lưu giữ các message không thể xử lý.
3. **Khả năng phục hồi (Resilience)**:
   - Cấu hình `AutomaticRecoveryEnabled = true` và `NetworkRecoveryInterval = 5s` trên RabbitMQ Client.
   - Cơ chế `Manual Ack` (`autoAck: false`): Nếu một Worker đang xử lý mà bị tắt đột ngột, RabbitMQ tự động thu hồi message và chuyển sang Worker còn sống khác.

---

## CHƯƠNG 4: KỊCH BẢN DEMO KHI BẢO VỆ ĐỒ ÁN

1. **Khởi chạy hệ thống**: Chạy `docker compose up -d --build`. Mở giao diện RabbitMQ (`http://localhost:15672`) và cửa sổ log của 2 Worker.
2. **Tạo chiến dịch**: Dùng `Admin.Client` chọn chức năng `1`, tạo chiến dịch với 15 người nhận (hệ thống tự động tạo các email thường và email mẫu có hậu tố `.fail` để minh họa lỗi tạm thời).
3. **Kích hoạt gửi**: Chọn chức năng `4` (`POST /campaigns/{id}/start`). Quan sát log 2 Worker (`Worker-Node-01` và `Worker-Node-02`) chia nhau xử lý song song kèm `CorrelationId`.
4. **Kiểm tra thống kê & chi tiết**: Chọn chức năng `2` và `5` để thấy rõ số lượng `Sent`, `Failed` và Worker nào đã xử lý từng người nhận.
5. **Chạy lại các job thất bại**: Chọn chức năng `6` (`POST /campaigns/{id}/retry-failed`), sau đó kiểm tra lại chức năng `5` để chứng minh các job lỗi tạm thời đã chuyển sang `Sent` với `RetryCount = 1`.
