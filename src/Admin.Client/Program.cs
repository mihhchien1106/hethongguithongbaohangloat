using System.Net.Http.Json;
using System.Text;
using Shared.Contracts.Models;

Console.OutputEncoding = Encoding.UTF8;
Console.InputEncoding = Encoding.UTF8;

var apiUrl = Environment.GetEnvironmentVariable("API_BASE_URL") ?? "http://localhost:5080";
using var http = new HttpClient { BaseAddress = new Uri(apiUrl) };

while (true)
{
    Console.WriteLine();
    Console.ForegroundColor = ConsoleColor.Cyan;
    Console.WriteLine("======================================================================");
    Console.WriteLine("   HỆ THỐNG QUẢN TRỊ CHIẾN DỊCH GỬI THÔNG BÁO HÀNG LOẠT (ADMIN CLI)   ");
    Console.WriteLine($"   Campaign API: {apiUrl}");
    Console.WriteLine("======================================================================");
    Console.ResetColor();
    Console.WriteLine(" 1. Tạo chiến dịch mới (Nhập tay hoặc chọn danh sách mẫu tự động)");
    Console.WriteLine(" 2. Xem danh sách chiến dịch & Thống kê (Pending / Sent / Failed)");
    Console.WriteLine(" 3. Xem chi tiết một chiến dịch");
    Console.WriteLine(" 4. Kích hoạt gửi chiến dịch (POST /campaigns/{id}/start)");
    Console.WriteLine(" 5. Tra cứu chi tiết kết quả gửi (GET /campaigns/{id}/deliveries)");
    Console.WriteLine(" 6. Chạy lại các job thất bại (POST /campaigns/{id}/retry-failed)");
    Console.WriteLine(" 0. Thoát chương trình");
    Console.WriteLine("----------------------------------------------------------------------");
    Console.Write("Chọn chức năng (0-6): ");

    var choice = Console.ReadLine()?.Trim();

    try
    {
        switch (choice)
        {
            case "1":
                await CreateCampaignAsync(http);
                break;
            case "2":
                await ListCampaignsAsync(http);
                break;
            case "3":
                await GetCampaignDetailAsync(http);
                break;
            case "4":
                await StartCampaignAsync(http);
                break;
            case "5":
                await GetDeliveriesAsync(http);
                break;
            case "6":
                await RetryFailedJobsAsync(http);
                break;
            case "0":
                Console.WriteLine("Đã thoát Admin Client.");
                return;
            default:
                Console.WriteLine("Lựa chọn không hợp lệ, vui lòng chọn lại.");
                break;
        }
    }
    catch (Exception ex)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"[Lỗi kết nối API]: {ex.Message}");
        Console.ResetColor();
    }
}

static async Task CreateCampaignAsync(HttpClient http)
{
    Console.Write("
Nhập tên chiến dịch (Mặc định: 'Thông báo Bảo trì Hệ thống Q4'): ");
    var name = Console.ReadLine();
    if (string.IsNullOrWhiteSpace(name)) name = "Thông báo Bảo trì Hệ thống Q4";

    Console.Write("Nhập tiêu đề (Mặc định: '[Quan trọng] Lịch nâng cấp máy chủ định kỳ'): ");
    var subject = Console.ReadLine();
    if (string.IsNullOrWhiteSpace(subject)) subject = "[Quan trọng] Lịch nâng cấp máy chủ định kỳ";

    Console.Write("Nhập nội dung (Mặc định: 'Hệ thống sẽ bảo trì từ 00:00 đến 02:00 sáng mai.'): ");
    var content = Console.ReadLine();
    if (string.IsNullOrWhiteSpace(content)) content = "Hệ thống sẽ bảo trì từ 00:00 đến 02:00 sáng mai.";

    Console.Write("Chọn kênh gửi (Email/SMS/Push - Mặc định: Email): ");
    var channel = Console.ReadLine();
    if (string.IsNullOrWhiteSpace(channel)) channel = "Email";

    Console.Write("Bạn muốn (1) Tự sinh danh sách người nhận mẫu hay (2) Nhập thủ công? [Mặc định: 1]: ");
    var mode = Console.ReadLine()?.Trim();

    var recipients = new List<RecipientDto>();

    if (mode == "2")
    {
        Console.WriteLine("Nhập danh sách theo cú pháp: email,tên (Nhập dòng trống để kết thúc):");
        while (true)
        {
            Console.Write("> ");
            var line = Console.ReadLine();
            if (string.IsNullOrWhiteSpace(line)) break;
            var parts = line.Split(',', 2);
            recipients.Add(new RecipientDto(parts[0].Trim(), parts.Length > 1 ? parts[1].Trim() : "Khách hàng"));
        }
    }
    else
    {
        Console.Write("Nhập số lượng người nhận cần sinh tự động (Mặc định: 15): ");
        if (!int.TryParse(Console.ReadLine(), out int count) || count <= 0) count = 15;

        for (int i = 1; i <= count; i++)
        {
            // Cố tình gài vài địa chỉ chứa chữ 'fail' để demo tính năng Retry dễ dàng
            string email = (i % 5 == 0)
                ? $"user{i}.fail@example.com"
                : $"user{i}@example.com";
            recipients.Add(new RecipientDto(email, $"Người nhận #{i}"));
        }
    }

    var req = new CreateCampaignRequest(name, subject, content, channel, recipients);
    var res = await http.PostAsJsonAsync("/campaigns", req);
    var body = await res.Content.ReadAsStringAsync();

    Console.ForegroundColor = ConsoleColor.Green;
    Console.WriteLine($"
[HTTP {(int)res.StatusCode}] Kết quả tạo chiến dịch:");
    Console.WriteLine(body);
    Console.ResetColor();
}

static async Task<List<CampaignSummaryResponse>> ListCampaignsAsync(HttpClient http)
{
    var list = await http.GetFromJsonAsync<List<CampaignSummaryResponse>>("/campaigns") ?? new();
    Console.WriteLine("
----------------------------------------------------------------------------------------------------------------");
    Console.WriteLine($"{"STT",-4} | {"Campaign ID",-36} | {"Tên chiến dịch",-28} | {"Trạng thái",-10} | {"Tổng",-5} | {"Pend",-5} | {"Sent",-5} | {"Fail",-5}");
    Console.WriteLine("----------------------------------------------------------------------------------------------------------------");

    for (int i = 0; i < list.Count; i++)
    {
        var c = list[i];
        var shortName = c.Name.Length > 28 ? c.Name[..25] + "..." : c.Name;
        Console.WriteLine($"{i + 1,-4} | {c.Id,-36} | {shortName,-28} | {c.Status,-10} | {c.TotalRecipients,-5} | {c.PendingCount,-5} | {c.SentCount,-5} | {c.FailedCount,-5}");
    }
    Console.WriteLine("----------------------------------------------------------------------------------------------------------------");
    return list;
}

static async Task<Guid?> SelectCampaignIdAsync(HttpClient http)
{
    var list = await ListCampaignsAsync(http);
    if (list.Count == 0)
    {
        Console.WriteLine("Chưa có chiến dịch nào. Hãy tạo chiến dịch trước (Phím 1).");
        return null;
    }

    Console.Write("Nhập STT chiến dịch (hoặc dán trực tiếp GUID, mặc định chọn #1): ");
    var input = Console.ReadLine()?.Trim();
    if (string.IsNullOrWhiteSpace(input)) return list[0].Id;

    if (int.TryParse(input, out int idx) && idx >= 1 && idx <= list.Count)
        return list[idx - 1].Id;

    if (Guid.TryParse(input, out Guid guid))
        return guid;

    Console.WriteLine("ID không hợp lệ.");
    return null;
}

static async Task GetCampaignDetailAsync(HttpClient http)
{
    var id = await SelectCampaignIdAsync(http);
    if (id is null) return;

    var res = await http.GetStringAsync($"/campaigns/{id}");
    Console.ForegroundColor = ConsoleColor.Yellow;
    Console.WriteLine("
Chi tiết chiến dịch:");
    Console.WriteLine(res);
    Console.ResetColor();
}

static async Task StartCampaignAsync(HttpClient http)
{
    var id = await SelectCampaignIdAsync(http);
    if (id is null) return;

    var res = await http.PostAsync($"/campaigns/{id}/start", null);
    var body = await res.Content.ReadAsStringAsync();

    Console.ForegroundColor = res.IsSuccessStatusCode ? ConsoleColor.Green : ConsoleColor.Red;
    Console.WriteLine($"
[HTTP {(int)res.StatusCode}] Phản hồi từ API:");
    Console.WriteLine(body);
    Console.ResetColor();
}

static async Task GetDeliveriesAsync(HttpClient http)
{
    var id = await SelectCampaignIdAsync(http);
    if (id is null) return;

    Console.Write("Lọc theo trạng thái (Bỏ trống = Tất cả, hoặc nhập Pending / Sent / Failed): ");
    var statusFilter = Console.ReadLine()?.Trim();

    var url = $"/campaigns/{id}/deliveries" + (string.IsNullOrWhiteSpace(statusFilter) ? "" : $"?status={statusFilter}");
    var deliveries = await http.GetFromJsonAsync<List<DeliveryDetailResponse>>(url) ?? new();

    Console.WriteLine("
-----------------------------------------------------------------------------------------------------------------------");
    Console.WriteLine($"{"Người nhận",-28} | {"Trạng thái",-10} | {"Retry",-5} | {"Xử lý bởi Worker",-18} | {"Ghi chú / Lỗi"}");
    Console.WriteLine("-----------------------------------------------------------------------------------------------------------------------");
    foreach (var d in deliveries)
    {
        if (d.Status == "Sent") Console.ForegroundColor = ConsoleColor.Green;
        else if (d.Status == "Failed") Console.ForegroundColor = ConsoleColor.Red;
        else Console.ForegroundColor = ConsoleColor.Yellow;

        Console.WriteLine($"{d.RecipientAddress,-28} | {d.Status,-10} | {d.RetryCount,-5} | {d.ProcessedByWorker ?? "-",-18} | {d.ErrorMessage ?? "OK"}");
        Console.ResetColor();
    }
    Console.WriteLine("-----------------------------------------------------------------------------------------------------------------------");
}

static async Task RetryFailedJobsAsync(HttpClient http)
{
    var id = await SelectCampaignIdAsync(http);
    if (id is null) return;

    var res = await http.PostAsync($"/campaigns/{id}/retry-failed", null);
    var body = await res.Content.ReadAsStringAsync();

    Console.ForegroundColor = ConsoleColor.Green;
    Console.WriteLine($"
[HTTP {(int)res.StatusCode}] Kết quả kích hoạt chạy lại job lỗi:");
    Console.WriteLine(body);
    Console.ResetColor();
}
