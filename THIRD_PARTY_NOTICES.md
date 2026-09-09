# Third-Party Notices and Provenance Status

BFI is independently developed by Zataralee and interoperates with Bambu Studio.
It is not affiliated with or endorsed by Bambu Lab or any filament manufacturer.

Company, product, printer, and filament names are used for identification and
compatibility. They remain the property of their respective owners. Manufacturer
temperature ranges, drying guidance, and material specifications are represented
as factual data with source references in `catalogs/README.md`; those facts and
source materials are not made proprietary by BFI's license.

Zataralee's original filament-library contributions and the documented `.bflib`
format are dedicated under CC0 1.0 Universal as described in
`LIBRARY_LICENSE.md`. That dedication applies only to rights Zataralee owns and
does not relicense the third-party material described here.

BFI is published as a self-contained .NET application. Microsoft .NET runtime
components remain governed by Microsoft's applicable licenses and notices.

## Bambu Studio profile-content review

Bambu Studio is distributed under AGPL-3.0. BFI does not link to Bambu Studio or
include its executable, but the current SUNLU package provenance review identified
startup-G-code fields carried forward from the legacy SUNLU package. At least one
of those fields exactly matches conditional startup G-code in an upstream Bambu
Studio profile.

Until that content is replaced, separately permitted, or distributed with confirmed
compliance, BFI's proprietary license does not claim ownership of or impose new
restrictions on those third-party portions. This notice is not a determination that
BFI as a whole is governed by AGPL-3.0; it records the exact unresolved component
question so it is not hidden by the license change.

Additional notices will be added as package and binary provenance is audited.
