<div align="center">
  <img src="./src/Trevunix/Assets/Logo.png" alt="Trevunix" width="140"/>

# Trevunix

Công cụ tối ưu và dọn dẹp Windows, mã nguồn mở.

[![Tải Trevunix](https://img.shields.io/github/v/release/TMH611205/Trevunix?label=T%E1%BA%A3i%20v%E1%BB%81&style=for-the-badge)](https://github.com/TMH611205/Trevunix/releases/latest/download/Trevunix.exe)
[![Giấy phép GPL-3.0](https://img.shields.io/badge/license-GPL--3.0-blue?style=for-the-badge)](./LICENSE)

</div>

## Tải về và sử dụng

1. Tải **[Trevunix.exe](https://github.com/TMH611205/Trevunix/releases/latest/download/Trevunix.exe)**. Không cần cài đặt, không cần cài .NET.
2. Mở file và chọn **Yes** khi Windows hỏi quyền Administrator.
3. Nếu Windows SmartScreen báo _"Windows protected your PC"_, chọn **More info** rồi **Run anyway**. Cảnh báo này xuất hiện vì file chưa được ký số. Trên Windows 11 có bật **Smart App Control**, Windows sẽ chặn hẳn file chưa ký số.

Mỗi lần mở, Trevunix tự kiểm tra và tải bản mới. Giữ nguyên tên file `Trevunix.exe` để tính năng này hoạt động.

Yêu cầu: Windows 10 hoặc 11 (x64).

## Tính năng

- **Tối ưu nhanh**: một nút dọn file tạm, giải phóng RAM và (tuỳ chọn) áp dụng các tinh chỉnh được khuyên dùng.
- **Tối ưu**: tinh chỉnh quyền riêng tư, quảng cáo, AI, hiệu năng và chơi game. Trevunix lưu cài đặt gốc và tạo điểm khôi phục trước khi đổi, nên mọi tinh chỉnh đều hoàn tác được.
- **Giao diện**: chế độ tối, màu nhấn, hiệu ứng trong suốt, tinh chỉnh thanh tác vụ và File Explorer.
- **Giải phóng tài nguyên**: RAM (tự động khi vượt ngưỡng), CPU, GPU và **Game Booster** (chế độ nguồn hiệu năng cao, đóng các ứng dụng đã chọn).
- **Dọn dẹp**: file tạm, báo cáo lỗi, bộ nhớ đệm Windows Update, trình duyệt, ứng dụng và Thùng rác. Có thể đặt lịch dọn tự động.
- **Dung lượng ổ đĩa**: thư mục và tệp lớn nhất, dung lượng theo loại tệp.
- **Ứng dụng & Khởi động**: bật/tắt chương trình chạy cùng Windows, gỡ ứng dụng cài sẵn.
- **Công cụ tiện ích**: đo tốc độ mạng, giám sát, tiến trình, sửa lỗi Windows (SFC, DISM), đổi DNS, sức khoẻ ổ đĩa và pin.
- **Chạy nền**: biểu tượng ở khay hệ thống, khởi động cùng Windows, cảnh báo khi ổ đĩa sắp đầy, ổ cứng báo lỗi hoặc máy quá nóng.

## Phát triển

Cần Windows và .NET SDK 10.

```bash
dotnet build Trevunix.slnx -c Release
```

```bash
dotnet run --project tests/Trevunix.Core.Tests -c Release
```

- `src/Trevunix.Core`: toàn bộ logic, không phụ thuộc giao diện.
- `src/Trevunix`: ứng dụng WPF (CommunityToolkit.Mvvm, WPF UI).
- `tests/Trevunix.Core.Tests`: test (xUnit v3).
- `build.cmd`: build một file `Trevunix.exe` duy nhất ở thư mục gốc để chạy thử. Bản này không tự cập nhật.

Chữ trên giao diện nằm trong `src/Trevunix/Resources/Strings.resx` (tiếng Anh) và `Strings.vi.resx` (tiếng Việt). Định dạng code bằng `csharpier format .` trước khi commit.

Mỗi lần đẩy code lên `main`, GitHub Actions build và chạy test. Bản phát hành chỉ được tạo khi chạy tay workflow **Release**.

## Đóng góp

Báo lỗi và góp ý qua [Issues](https://github.com/TMH611205/Trevunix/issues). Pull request được hoan nghênh; mọi đóng góp được phát hành theo cùng giấy phép GPL-3.0.

## Giấy phép

Copyright (c) 2026 Bii.

Trevunix là phần mềm tự do theo giấy phép [GNU General Public License v3.0](./LICENSE). Bạn được dùng, sao chép, sửa đổi và phân phối lại, với điều kiện bản phân phối lại (kể cả bản đã sửa) cũng phải công khai mã nguồn theo GPL-3.0. Các thư viện bên thứ ba giữ giấy phép riêng, xem [THIRD-PARTY-NOTICES.md](./THIRD-PARTY-NOTICES.md).
