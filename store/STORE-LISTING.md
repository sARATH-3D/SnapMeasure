# Microsoft Store listing – SnapMeasure

## Next listing update (after 1.3.2 is live) – no new package needed

Partner Center → SnapMeasure → *Start update* → Store listings. Keep the package as it is.

### Short description (both languages; this field is empty today)
* **English:** Screenshot, measure and take off quantities: dimensions, areas with cut-outs, lengths, angles, counts and drawing scales, with mark-up, OCR and an on-screen ruler.
* **한국어:** 화면을 캡처하고 측정하여 물량을 산출합니다: 치수, 개구부를 뺀 면적, 길이, 각도, 개수, 도면 축척, 마크업, 텍스트 인식(OCR), 화면 눈금자.

### Search terms (up to 7 each)
* **English:** quantity takeoff · measure PDF · screenshot measure · construction estimating · area calculator · drawing scale · markup
* **한국어:** 물량 산출 · 적산 · 도면 측정 · 면적 계산 · 화면 캡처 · PDF 측정 · 마크업

### Trailer and artwork (already made, in `Share\Store\Listing`)
* Trailer: `SnapMeasure-trailer-1080p.mp4` with `trailer-thumbnail-1920x1080.png` as its thumbnail.
* 16:9 Super hero art: `hero-1920x1080.png` (needed for the trailer to show at the top of the listing).
* Use the same files for the Korean listing.

### Screenshot captions (screenshot order: facade, area/magnifier, ruler/protractor)
| # | English | 한국어 |
|---|---------|--------|
| 1 | Takeoff on a facade drawing: areas with cut-outs, dimensions and counts | 입면도 물량 산출: 개구부를 뺀 면적, 치수, 개수 |
| 2 | Gross, openings and net area, with a magnifier for exact points | 총면적, 개구부, 순면적 – 정확한 점을 위한 확대경 |
| 3 | On-screen ruler and protractor over any app | 어떤 앱 위에서도 쓰는 화면 눈금자와 각도기 |

On the Korean listing, put the facade screenshot first (it was uploaded third).
Later: take Korean screenshots with Settings → General → Language = 한국어, and 1.3.2 screenshots (coloured tool icons).

---

## Original 1.3.0 listing text

Text to paste into Partner Center → the submission's *Store listings* (English).

**Package to upload:** `Share\Store\SnapMeasure-1.4.2.0-x64.msix` (build it with `store\build-store.cmd`).
It is not signed; the Store signs it.

## Product name
SnapMeasure

## Short description (max 200 characters)
Screenshot, measure and take off quantities: dimensions, areas with cut-outs, lengths, angles, counts and drawing scales, with mark-up, OCR and an on-screen ruler.

## Description
SnapMeasure is a screenshot and quantity takeoff tool for anyone who works with drawings, PDFs and plans.

Capture a region, window or screen (PrtSc), a scrolling page, or open an image or PDF page. Then measure straight on the picture:
• Dimensions, path lengths, perimeters and areas with cut-outs (gross, cut-outs and net)
• Angles, radius and diameter, X / Y coordinates
• Number and letter markers that count themselves
• Drawing scales: 1:50 on A1, 1:100 at 300 dpi, exact PDF page scale, or calibrate from a known length
• Copy every value, count and label to Excel in one click

Mark up with arrows, lines, polylines, arcs, rectangles, ellipses, polygons, revision clouds, text, speech bubbles, stamps (Approved, Revise, For construction …), notes, a highlighter pen, and focus effects (spotlight, grayscale, magnify, pixelate).

Also included: text recognition (OCR) to copy text from any picture, pinned pictures that stay on top, and an on-screen ruler and protractor that float over any app.

No account, no telemetry, no ads. Your pictures stay on your PC.

## What's new in this version
**1.4.2 (English):** New: a Fill tool (paint bucket) for outlines you draw, symbol markers, a search box for tools, find and replace, a symbol picker, your own coordinate origin, remove background, and text position options. If you update from 1.4.1: Z now zooms to an area and Shift+Z is the stamp; Ctrl+Minus only removes space you added.

**1.4.2 (한국어):** 새 기능: 직접 그린 윤곽선을 채우는 채우기 도구(페인트 통), 기호 마커, 도구 검색 상자, 찾기 및 바꾸기, 기호 선택기, 좌표 원점 값 지정, 배경 제거, 텍스트 위치 옵션. 1.4.1에서 업데이트하는 경우: 이제 Z는 영역 확대, Shift+Z는 스탬프입니다. Ctrl+Minus는 추가한 여백만 제거합니다.

**1.4.1 (English):** PDF pages now always open at the sharpness you pick, so the PDF page scale is exact on every PC. Scrolling capture no longer stops early in apps whose background shade changes while they scroll (such as Notepad on Windows 11). New quick user guide and trailer video.

**1.4.1 (한국어):** PDF 페이지가 선택한 해상도 그대로 열려 어느 PC에서나 PDF 축척이 정확합니다. 스크롤하는 동안 배경 색조가 바뀌는 앱(Windows 11 메모장 등)에서 스크롤 캡처가 일찍 멈추던 문제를 고쳤습니다. 빠른 사용자 가이드와 소개 영상이 추가되었습니다.

## Keywords (up to 7)
screenshot; measure; quantity takeoff; markup; PDF; OCR; ruler

## Category
Productivity (subcategory: none) – or Utilities & tools

## Privacy policy URL
https://github.com/sARATH-3D/SnapMeasure/blob/main/PRIVACY.md
(this page exists only after PRIVACY.md is pushed to GitHub)

## Website / support
https://github.com/sARATH-3D/SnapMeasure – support: https://github.com/sARATH-3D/SnapMeasure/issues

## Pricing
Free

## Restricted capability: runFullTrust – justification
SnapMeasure is a classic Windows desktop (Win32) app packaged as MSIX. It needs full trust to register the global capture
keys (PrtSc, Ctrl+Shift+1 …), capture the screen, show its tray icon and draw the always-on-top on-screen ruler and protractor.

## System requirements
Windows 10 version 1809 or later, Windows 11. Uses the .NET Framework 4.x that is built into Windows.

## Screenshots
At least one 1366×768 or larger screenshot of the editor is needed (e.g. a takeoff on a facade drawing, like
`Share\Demos\Demo facade E1.png`). Take them from the app; the Store does not accept mock-ups with other apps' content.
