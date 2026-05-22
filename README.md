# HoloPDFCreator

PDF 뷰어, 편집, 이미지 품질 개선 기능을 갖춘 Windows 데스크톱 애플리케이션입니다.

## 기능

### PDF Reader
- PDF 파일 열기 (파일 탐색기 또는 드래그 앤 드롭)
- 페이지 탐색 및 줌 조절
- PDF 생성 및 편집
- 북마크, 텍스트 주석, 메모 관리

### Image Adjuster
- 이미지 파일 열기
- 밝기 / 대비 / 획 굵기 / 자동 레벨 슬라이더 조절
- 원본과 결과 Before/After 미리보기

## 기술 스택

| 항목 | 내용 |
|------|------|
| 프레임워크 | .NET 8 WPF (`net8.0-windows10.0.19041.0`) |
| PDF 렌더링 | Windows.Data.Pdf (WinRT API) |
| PDF 생성/편집 | PDFsharp-WPF 6.1.1 |
| 이미지 처리 | System.Drawing.Common 8.0.0 |
| PDF 파싱 | PdfPig 0.1.9 |

## 요구 사항

- Windows 10 버전 1903 이상 (빌드 19041+)
- .NET 8 Runtime

## 빌드 및 실행

```bash
git clone https://github.com/wanakt/HoloPDFCreator.git
cd HoloPDFCreator
dotnet run
```

또는 Visual Studio 2022에서 `HoloPDFCreator.sln`을 열어 실행하세요.
