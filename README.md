# HotPatchKit — vá nóng từng hàm cho Unity IL2CPP

Cập nhật mã game **không phát hành lại bản cài**. Mã game vẫn dịch sẵn (AOT) trong bản cài nên chạy nhanh như bản thường.
Bản vá chỉ chứa phần mã đã đổi; phần đó chạy thông dịch qua [HybridCLR](https://github.com/focus-creative-games/hybridclr)
(bản cộng đồng, MIT), phần không đổi vẫn chạy mã máy.

Tách từ hệ thống cập nhật mã của dự án Game, chạy thật trên Android / iOS / Windows. Phần vá từng hàm là mã tự viết, không dùng
bản DHE trả phí của HybridCLR.

| Thư mục | Nội dung |
|---|---|
| `Packages/com.hotpatchkit/` | gói Unity: `Runtime/` (nằm trong app) + `Editor/` (dựng, sinh bản vá) |
| `Tools/hotpatch.ps1` | lệnh dòng lệnh: cài, dựng, xuất bản, liệt kê |
| `Sample/` | project mẫu, dùng để thử trọn vòng |

---

## 1. Cơ chế

1. **Lúc dựng bản cài**, ngay sau khi Unity biên dịch và trước IL2CPP, mỗi hàm của assembly mã game được chèn ở đầu một phép
   kiểm ô vá:
   ```csharp
   if (HotPatch.On) { var f = HotPatch.Slots[id]; if (f != null && !HotPatch.TakeSkip(id)) return f(this, args); }
   ```
   Chưa có bản vá thì `On = false`: mỗi lần gọi hàm chỉ tốn thêm một lần đọc biến static.
2. DLL mã game gốc (chưa chèn) và bảng mã hàm được lưu vào **kho bản gốc**, theo **mã bản cài**. Mã bản cài là chuỗi băm
   của mã game + phần AOT + Unity/HybridCLR + dev/phát hành.
3. **Lúc xuất bản**, bộ sinh so mã game hiện tại với DLL gốc của từng bản cài và sinh assembly `HotPatchGen` chỉ chứa phần khác:
   - hàm đổi, hàm mới;
   - lớp mới, lớp đổi lớp cha (thay cả lớp);
   - lambda mới (ghép theo nội dung, lambda không đổi vẫn chạy mã máy);
   - biến thành viên mới (lưu ở kho phụ), biến static mới;
   - override mới.

   Bản vá được nén, mã hoá AES-256, ký RSA-3072, rồi ghi ra `<cdnDir>/<nền tảng>/<mã bản cài>/`.
4. **Lúc mở app**, bộ nạp làm các bước sau, trước khi chạy bất kỳ mã game nào:
   1. Hỏi CDN file `version.json`.
   2. Kiểm chữ ký. Có bản mới hơn thì tải về, kiểm băm, lưu vào máy.
   3. Nạp bản vá qua HybridCLR và gán vào các ô.
   4. Gọi hàm vào game (`entry`).

   Mất CDN thì dùng bản đã tải lần trước. Chưa tải lần nào thì chạy mã của bản cài. Không bao giờ chặn người chơi.

## 2. Yêu cầu

- Unity 2021.3 trở lên, **IL2CPP**. Đã chạy thật trên Unity 6000.3.
- HybridCLR **v9.0.3** (gói git). Gói HybridCLR bản khác cũng có thể chạy, nhưng chưa kiểm.
- **Mã game nằm trong một asmdef riêng** (vd `Game.asmdef`). Mọi hàm trong asmdef đó vá được. Các phần khác là AOT, không vá
  được: `Assembly-CSharp`, asmdef khác, package, plugin.
- **Cảnh đầu không chứa component của mã game.** Game bắt đầu từ hàm `entry`, gọi sau khi đã cài bản vá. Component có sẵn
  trong cảnh sẽ chạy `Awake` / `Start` trước khi bản vá kịp cài.
- Máy dựng:
  - `git` (để cài HybridCLR);
  - module nền tảng của Unity (Windows / Android / iOS);
  - toolchain C++ của IL2CPP: Visual Studio C++ trên Windows; Xcode cho iOS.

## 3. Cài vào project

**a) `Packages/manifest.json`**: thêm hai dòng. Đường dẫn `file:` tính từ thư mục `Packages` của project. Có thể chép thư mục
`com.hotpatchkit` vào `Packages/` của project rồi bỏ dòng thứ hai.

```json
"com.code-philosophy.hybridclr": "https://github.com/focus-creative-games/hybridclr_unity.git#v9.0.3",
"com.hotpatchkit": "file:D:/HotPatchKit/Packages/com.hotpatchkit",
```

**b) Tạo file cấu hình**: menu **Tools → HotPatchKit → Tạo file cấu hình**. File tạo ra là
`Assets/HotPatchKit/Resources/HotPatchKit.json`. Sửa các ô sau:

| Ô | Ý nghĩa |
|---|---|
| `gameAssembly` | tên asmdef mã game (vd `"Game"`) |
| `entry` | hàm vào game: static, không tham số, `"Namespace.Lớp.Hàm"` |
| `cdnUrl` | gốc CDN bản vá, vd `"https://cdn.example.com/hotpatch"`. App hỏi `<cdnUrl>/<nền tảng>/<mã bản cài>/version.json` |
| `autoBoot` | `true`: kit tự nạp bản vá rồi gọi `entry`. `false`: tự gọi (mục 6) |
| `keyDir` | thư mục khoá + kho bản gốc, **ngoài repo**. Mặc định `%USERPROFILE%/.hotpatchkit/<tên sản phẩm>` |
| `cdnDir` | nơi ghi bản vá khi xuất bản (mặc định `HotPatchCDN` trong project) |
| `checkTimeout` / `downloadTimeout` | thời gian chờ CDN / tải gói (giây) |
| `keepBases` | không chỉ rõ bản cài thì vá cho N bản mới nhất |
| `runtimeIdExtraFiles` | file ngoài mã nguồn mà đổi thì bản cài cũ không vá được nữa (mặc định `Assets/link.xml`) |
| `aotMetadata` | DLL AOT nhúng làm metadata bổ sung cho generic |
| `hardenNative` | gia cố chống dịch ngược (mục 9), mặc định tắt |

**c) Cài HybridCLR**: menu **Tools → HotPatchKit → Cài HybridCLR**, hoặc dòng lệnh:
```powershell
D:\HotPatchKit\Tools\hotpatch.ps1 -Project D:\MyGame -Install
```
- Lệnh này `git clone` hybridclr và il2cpp_plus vào `Library/HybridCLR`. Mỗi máy dựng chạy một lần, và chạy lại sau khi nâng gói
  HybridCLR.
- Lần đầu lệnh tạo khoá trong `keyDir`. **Sao lưu ngay thư mục đó** (mục 8).
- HybridCLR bật thì mọi lần dựng IL2CPP dùng il2cpp đã vá trong `Library/HybridCLR`. Dựng thường bằng File → Build vẫn chạy,
  nhưng bản cài đó không vá được.

## 4. Dựng bản cài

Bản cài phát cho người chơi **phải** dựng qua HotPatchKit. Chỉ đường này mới chèn ô vá và lưu bản gốc.

```powershell
hotpatch.ps1 -Project D:\MyGame -Build -Out D:\MyGame\Build\win                 # Windows, phát hành (-Dev: bản dev)
hotpatch.ps1 -Project D:\MyGame -Build -Target android -Out D:\MyGame\Build\game.apk
hotpatch.ps1 -Project D:\MyGame -Build -Target ios -Out D:\MyGame\Build\ios       # ra project Xcode
```

- Trong Unity: **Tools → HotPatchKit → Dựng bản cài (dev / phát hành)**.
- Script dựng riêng của project thì gọi `HotPatchKit.Editor.HotPatchBuild.BuildPlayer(BuildPlayerOptions)` thay cho
  `BuildPipeline.BuildPlayer`.
- Mỗi lần dựng in ra **mã bản cài**, ví dụ `[hotpatch] bản cài win64/1a2b3c4d5e6f xong`.
  - Ghi lại mã của bản phát cho người chơi.
  - Xem lại bằng `hotpatch.ps1 -List`, hoặc trong log app: dòng `[hotpatch] … (win64/<mã>)`.
- Bản dev và bản phát hành là hai bản cài khác nhau. Lệnh xuất bản tự dịch mã đúng kiểu của từng bản.

## 5. Sinh bản vá và đưa lên CDN

Sửa mã trong asmdef mã game, rồi chạy:

```powershell
hotpatch.ps1 -Project D:\MyGame -Publish                                    # Windows: các bản cài mới nhất cùng runtime
hotpatch.ps1 -Project D:\MyGame -Publish -Target all -Base 1a2b3c4d5e6f,0f9e8d7c6b5a   # đúng các bản cài đã phát
hotpatch.ps1 -Project D:\MyGame -List -Target all
```

Mỗi bản cài in một dòng kết quả:

- `xuất bản … bản vá N KB → gói N KB (hàm đổi …)`: có bản vá.
- `mã game không khác bản cài — bỏ qua`: không có gì đổi.
- `KHÔNG vá được — N lỗi` kèm tên hàm / lớp: thay đổi này không vá được (mục 7), phải phát hành bản cài mới.
- `KHÔNG vá được — runtime … khác`: từ lúc dựng bản cài đó, phần AOT đã đổi (mục 7), nên bản cài đó không nhận vá nữa.

Bản vá nằm ở `<cdnDir>/<nền tảng>/<mã bản cài>/patch.bytes` và `version.json`. **Đẩy nguyên cấu trúc này** lên đúng đường
`cdnUrl`. Mỗi nền tảng, mỗi bản cài có bản vá riêng; không dùng chung.

**Gỡ bản vá lỗi:** xoá file trên CDN không gỡ được ở máy đã tải. Cách đúng là sửa mã rồi vá lại; bản mới hơn sẽ đè bản cũ.
Muốn quay về mã gốc thì checkout mã cũ rồi vá lại.

## 6. App nhận bản vá

- `autoBoot: true`: không cần làm gì thêm. Kit chạy sau khi cảnh đầu nạp xong, hiện dòng trạng thái khi tải lâu, cài bản vá
  rồi gọi `entry`.
- `autoBoot: false`: tự gọi trong màn khởi động của project. Màn khởi động phải là mã AOT, **không** nằm trong asmdef mã game:
  ```csharp
  yield return HotPatchKit.HotPatchLoader.Run();          // hoặc Run("https://cdn…") để đổi gốc CDN lúc chạy
  Debug.Log(HotPatchKit.HotPatchLoader.Result);          // Installed / Version / Error
  HotPatchKit.HotPatchLoader.InvokeEntry();              // hoặc tự gọi vào game
  ```
  Trong lúc chờ, `HotPatchLoader.Status` / `Progress` cho biết tiến độ.
- Dòng lệnh (để thử):
  - `-hotpatch-cdn <url>`: đổi gốc CDN;
  - `-hotpatch-local`: không hỏi CDN;
  - `-hotpatch-cache <thư mục>`: đổi kho trên máy (mặc định `persistentDataPath/HotPatchKit`).
- Người chơi đang trong game sẽ nhận bản vá ở lần mở app kế tiếp.

## 7. Vá được và không vá được

**Vá được** (trong asmdef mã game):
- sửa thân hàm, thêm hàm, thêm lớp;
- thêm biến thành viên vào lớp có sẵn (lưu ở kho phụ theo đối tượng);
- thêm biến static;
- lambda / closure mới hoặc đã sửa;
- override mới của hàm virtual trong mã game;
- đổi lớp cha (thay cả lớp).

**Không vá được** (bộ sinh báo rõ; phải phát hành bản cài mới):
- Sửa phần AOT: mọi assembly khác mã game, package, plugin, `link.xml`, đổi Unity / HybridCLR / `hardenNative`.
- Static constructor của lớp có sẵn, kể cả đổi giá trị khởi tạo của biến static có sẵn. Biến static **mới** nên để không có
  giá trị khởi tạo rồi lấy lười.
- Constructor mới trên lớp có sẵn.
- Hàm có tham số `ref` / `out` / con trỏ.
- **Lấy địa chỉ biến thành viên mới kiểu giá trị** (lệnh IL `ldflda`). Biến mới nằm ở kho phụ nên không có địa chỉ. Lỗi hay
  gặp nhất: `"…" + obj.NewInt`, `obj.NewInt.ToString()`, gọi hàm của struct, truyền `ref`. Cách tránh: chép ra biến cục bộ
  trước (`var n = obj.NewInt; "…" + n`). Bộ sinh báo rõ tên biến và hàm.
- Hàm của struct, của lớp generic, hàm generic.
- Override mới của hàm virtual nằm ngoài mã game (vd `MonoBehaviour` / `object`), bỏ override có sẵn.
- Dùng API .NET / Unity mà bản cài đã strip.

Mẹo:
- Thêm tính năng lớn thì đặt vào **lớp mới**.
- Component mới (`MonoBehaviour` mới) thêm bằng `AddComponent` trong mã, không đặt sẵn trong cảnh / prefab của bản cài.

## 8. Khoá và sao lưu

Thư mục `keyDir` (đổi được bằng biến môi trường `HOTPATCHKIT_KEYDIR`):

| File | Mất thì |
|---|---|
| `sign-private.xml` (khoá ký RSA bí mật) | **mọi bản cài đã phát không nhận bản vá nữa** |
| `aes.key` | bản vá mới không giải được ở bản cài cũ |
| `obf-key.txt` (khi bật gia cố) | bản cài / bản vá cũ không khớp |
| `base/<nền tảng>/<mã bản cài>/` (kho bản gốc) | bản cài đó không vá được |

- Sao lưu cả thư mục. Máy dựng khác cần đúng thư mục này.
- **Không commit, không đưa lên CDN.**
- App chỉ chứa khoá công khai: ai sửa được CDN mà không có khoá ký thì không đưa được mã lạ vào app.

## 9. Gia cố chống dịch ngược (tuỳ chọn)

`hardenNative: true`:
- mã hoá `global-metadata.dat` của bản cài: Il2CppDumper không đọc được (Windows / iOS; APK Android chưa hỗ trợ);
- bọc DLL bản vá và metadata AOT bằng định dạng riêng: file tải từ CDN / nằm trong bản cài, và mảng byte đưa vào `Assembly.Load`,
  không phải DLL .NET. **Nhưng** il2cpp giải mã tại chỗ vào bộ nhớ riêng của nó và giữ suốt phiên chạy: dump bộ nhớ native rồi dò
  header PE vẫn lấy ra được DLL mở bằng dnSpy (chỉ phần có trong bản vá / metadata AOT, không phải toàn bộ mã game).

Mã giải mã được chèn vào cây il2cpp trong `Library/HybridCLR` (tự áp lại mỗi lần dựng). Khoá nằm trong app, nên đây chỉ là
làm khó, không phải bảo mật tuyệt đối. Đoạn neo khớp HybridCLR 9.0.3 / il2cpp Unity 6000.3; bản khác báo "không thấy đoạn để
vá" thì sửa hằng số cuối `Editor/HotPatchNative.cs`. Bật / tắt hoặc đổi khoá thì phải phát hành bản cài mới.

Dữ liệu được bọc trước khi nén nên gần như không nén được: gói vá to hơn (mẫu: 1,3 KB → 3,1 KB), metadata AOT nhúng trong bản cài
cũng to hơn (mẫu: 0,9 MB → 2,3 MB).

## 10. Chi phí

Đo trên Game (~46.000 hàm):
- không có bản vá: phần vẽ +~0,3 ms/khung, `GameAssembly` +17%;
- hàm đã vá chạy thông dịch, chậm hơn mã máy vài lần, nên chỉ ảnh hưởng phần đã sửa;
- cài bản vá lúc mở app ~100–200 ms;
- phải chờ CDN tối đa `checkTimeout` giây.

## 11. Project mẫu (`Sample/`)

Project Unity 6000.3, mã game là `Assets/Game` (asmdef `Game`). Lần đầu Unity tải HybridCLR từ GitHub và tạo lại `Library`.

```powershell
$u = 'E:\Unity\6000.3.28f1\Editor\Unity.exe'      # Unity của máy (hoặc đặt HOTPATCHKIT_UNITY)
D:\HotPatchKit\Tools\hotpatch.ps1 -Project D:\HotPatchKit\Sample -Unity $u -Install
D:\HotPatchKit\Tools\hotpatch.ps1 -Project D:\HotPatchKit\Sample -Unity $u -Build -Out D:\HotPatchKit\Sample\Build\win
# chạy Build\win\HotPatchSample.exe → "v1 — mã của bản cài"
# chép Sample\SampleGame.v2.cs.txt đè Assets\Game\SampleGame.cs, rồi:
D:\HotPatchKit\Tools\hotpatch.ps1 -Project D:\HotPatchKit\Sample -Unity $u -Publish
python -m http.server 8788 --directory D:\HotPatchKit\Sample\HotPatchCDN     # CDN thử (cdnUrl của mẫu: http://127.0.0.1:8788)
# chạy lại HotPatchSample.exe → "v2 — đã vá: 44, Value=21, lần gọi 1"; log: "[hotpatch] đã cài bản vá cdn …"
```

`SampleGame.v2.cs.txt` có đủ các loại thay đổi: sửa thân hàm, thêm hàm, thêm lớp, thêm biến thành viên vào lớp có sẵn, lambda mới.
Thêm cờ `-logFile -` khi chạy exe để xem log ngay trong cửa sổ lệnh.

**Đã kiểm (2026-11-13, Windows IL2CPP, Unity 6000.3.28f1, HybridCLR 9.0.3):**

| Trường hợp | Kết quả |
|---|---|
| Chưa có bản vá, CDN tắt | chạy mã bản cài |
| Có bản vá trên CDN | tải, kiểm chữ ký + băm, cài (~70–90 ms), chạy mã mới |
| Mở lại không mạng | dùng bản đã tải trong máy |
| `version.json` bị sửa | sai chữ ký → bỏ, chạy mã bản cài |
| `patch.bytes` bị sửa | sai băm → bỏ, chạy mã bản cài |
| `hardenNative: true` | metadata mã hoá, bản vá bọc; cài và chạy đúng |
| Thay đổi không vá được | bộ sinh báo rõ hàm / biến (vd lấy địa chỉ biến mới) |

Android / iOS: cùng cơ chế đã chạy thật trong Game, nhưng bản kit mới thử trên Windows. Lần đầu dùng cho Android / iOS, thử
trọn vòng như trên trước khi phát hành.

## 12. Khác bản trong Game

Bản kit chỉ giữ phần vá nóng, bỏ các phần riêng của Game:
- chế độ "full" (cả assembly mã game nạp nóng);
- `client.json` bắt cập nhật bản cài;
- file cấu hình máy chủ ký trên CDN;
- màn khởi động riêng.

Tên trong mã cũng đổi:

| Game | HotPatchKit |
|---|---|
| `GameBoot.HotPatch` | `HotPatchKit.HotPatch` |
| `GamePatch` | `HotPatchGen` |
| `xinanua.bytes` | `patch.bytes` |
| đường CDN `hd/<nền tảng>/…` | `<cdnUrl>/<nền tảng>/…` |

Khoá của mỗi project tạo mới, không dùng chung với Game.
