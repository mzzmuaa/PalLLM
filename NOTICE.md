# NOTICE

PalLLM is an independent, unofficial, community-built tool.

## Not affiliated

PalLLM is a third-party integration project. It is **not affiliated with,
endorsed by, sponsored by, or approved by** any game publisher, game
developer, middleware vendor, model provider, hosting service, or any
other third party whose products, APIs, or file-format conventions it
interoperates with.

Any references in the source code, configuration defaults, or
documentation to third-party classes, API paths, file-path conventions,
HTTP endpoints, or model tags are **interoperability references**: they
exist solely so PalLLM can read from, or write to, the corresponding
interface at runtime. Inclusion of such references does not imply a
partnership, endorsement, license, or permission from the owner of the
named interface.

## Trademarks

All trademarks and registered trademarks mentioned in this repository
are the property of their respective owners. PalLLM claims no rights in,
and asserts no association with, those marks.

## Third-party components

PalLLM depends at runtime on components it does not own and does not
redistribute. Those components are covered by their own licenses. See
[`THIRD_PARTY_NOTICES.md`](THIRD_PARTY_NOTICES.md) for the list and
pointers to each component's license.

## Player responsibility

PalLLM is a modification tool. The user is responsible for:

- Complying with the End User License Agreement, Terms of Service, and
  any other applicable terms of any game, service, or software PalLLM
  is used alongside.
- Ensuring that the operator is permitted to install, run, and modify
  each component on the machine the operator controls.
- Any consequences of enabling the opt-in automation, vision, or TTS
  surfaces.

PalLLM ships with every opt-in surface disabled by default. Each is
reversible via a config flag with no persistent state migration. See
[`docs/OPERATIONS.md`](docs/OPERATIONS.md) for the full opt-in matrix.

## No warranty

PalLLM is provided "as is" with no warranty of any kind. See
[`LICENSE`](LICENSE) for the full terms.
