# 외부 구성 요소

## Pretendard

- 출처: https://github.com/orioncactus/pretendard
- 사용 파일: `Pretendard-Regular.otf`, `Pretendard-SemiBold.otf`, `Pretendard-Bold.otf`
- 사용권: SIL Open Font License 1.1
- 저작권 및 사용권 전체: [OFL.txt](src/Freshet/Assets/Fonts/OFL.txt)

OFL 1.1은 무료·유료 소프트웨어에 글꼴을 포함해 배포하는 것을 허용합니다. 글꼴 자체만 판매할 수 없으며 각 배포본에 저작권과 사용권을 포함해야 합니다. 글꼴 수정본에는 예약된 글꼴 이름 제한이 적용됩니다. Freshet에는 글꼴을 WPF 리소스로 포함하며 글꼴 소프트웨어를 수정하지 않습니다.

‘Pretendard 글꼴 사용권’이라는 버튼 이름 자체가 의무인 것은 아닙니다. 사용자가 고지를 쉽게 확인할 수 있도록 현재 설정 화면의 원문 보기와 배포 ZIP의 `Pretendard-OFL.txt`를 유지합니다. 글꼴 사용권 때문에 Freshet 소스 코드까지 OFL로 공개할 의무는 없습니다.

검토 기준: [Pretendard LICENSE](https://github.com/orioncactus/pretendard/blob/main/LICENSE), [SIL OFL FAQ](https://openfontlicense.org/ofl-faq/). 확인일: 2026-11-10.

## .NET 런타임

Freshet은 .NET 및 Windows Desktop 런타임을 포함한 단일 EXE로 배포됩니다. 런타임 패키지의 MIT 사용권과 제3자 고지를 `licenses/`에 포함합니다. 빌드 스크립트는 복원한 런타임 패키지에서 원문을 복사합니다. Freshet 소스 공개 범위와 외부 구성 요소의 사용권은 별개입니다.

## 아이콘

앱 아이콘과 UI 벡터 아이콘은 프로젝트에서 직접 그린 원본입니다. 앱 아이콘은 `scripts/make-icon.ps1`이 생성합니다. 업데이트 대상 앱의 아이콘은 사용자 PC의 설치 정보에서 실행 시 읽으며 배포물에 복사하지 않습니다.
