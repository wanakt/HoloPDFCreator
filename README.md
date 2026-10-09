# HoloPDFCreator

PDF 보기·편집, 이미지 보정, OCR(글자 인식)로 검색되는 PDF 만들기를 갖춘 Windows 데스크톱 애플리케이션입니다.

## 기능

### PDF Reader
- PDF 열기(파일 탐색기·드래그 앤 드롭·명령줄 인자), 이미지 여러 장을 PDF로 만들기
- 이어 보기, 확대·축소, 축소판 패널
- 쪽 편집: 끌어서 순서 바꾸기, 삭제, 회전, 자르기(CropBox), 이미지 끼워 넣기, 쪽을 이미지·PDF로 뽑기
- 글자 선택·복사, 문서 전체 찾기
- 형광펜·밑줄·취소선, 메모, 선·상자·원 그리기 — 저장할 때 PDF 주석으로 넣음
- 책갈피: PDF 목차 읽기, 추가·이름 변경·하위 책갈피, 끌어서 옮기기 — 저장할 때 PDF 목차로 씀
- 이미지 보정: 밝기·대비·획 굵기·자동 레벨·언샤프 마스크를 쪽별·전체·범위·홀짝 쪽에 적용, 저장할 때 반영
- 되돌리기·다시 하기

### OCR
- 언어: 라틴, 한국어(한자 보조 인식 포함), 중국어, 일본어
- 모델 크기: Mobile(빠름) / Full(정확, PP-OCRv5 서버 모델)
- 현재 쪽·전체·쪽 범위 실행, 작업자 수 지정, 기울기 바로잡기
- 결과 글자 바로 고치기, 복사, TXT 저장
- 보이지 않는 글자 층을 넣어 검색되는 PDF로 저장(회전·자른 쪽 지원)

## 기술 스택

| 항목 | 내용 |
|------|------|
| 프레임워크 | .NET 8 WPF (`net8.0-windows10.0.19041.0`) |
| PDF 렌더링 | Windows.Data.Pdf (WinRT API) |
| PDF 생성/편집 | PDFsharp-WPF 6.1.1 |
| PDF 파싱 | PdfPig 0.1.9 |
| OCR | RapidOcrNet 2.0.0 + Microsoft.ML.OnnxRuntime 1.24.4 (PaddleOCR PP-OCRv3/v4/v5 ONNX 모델) |
| 이미지 처리 | System.Drawing.Common 8.0.0, SkiaSharp |

언어별 인식 모델은 `Models/v3`, `Models/v4`, `Models/v5`에 있고 빌드할 때 출력 폴더의 `models/`로 복사됩니다.

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
