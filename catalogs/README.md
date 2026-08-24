# Manufacturer Catalog

This release contains 243 filament product families from nine manufacturers. Each `.bflib` is printer-neutral. The importer reads the printer models and nozzle variants enabled in the user's Bambu Studio setup and creates compatible child profiles at installation time.

| Manufacturer | Products | Bambu Studio baseline | Package purpose | Official sources |
| --- | ---: | --- | --- | --- |
| SUNLU | 40 | Existing custom library | Migrate the original catalog to the printer-neutral format while retaining SUNLU PLA, PETG, ABS, ASA, PA, and TPU groups. | [Filament catalog](https://www.sunlu.com/collections/1?name=Filaments), [usage and drying guide](https://www.sunlu.com/wiki/filament-usage-guide), [material parameter table](https://www.sunlu.com/wiki/fdm-performance-comparison) |
| eSUN | 36 | PLA+ | Preserve the built-in PLA+ name and add consumer, engineering, composite, flexible, and support families. | [Engineering and functional materials](https://www.esun3d.com/uploads/eSUN-3D-EngineeringFunction-Series-Materials-Products-Introduction_3.3.pdf), [drying guide](https://www.esun3d.com/uploads/eBOX-Pro-User-Guide-ENDE_6.3.pdf) |
| Overture | 14 | PLA, Matte PLA | Preserve both built-in names and add the remaining products in Overture's print-settings chart. | [Filament cheat sheet](https://wiki.overture3d.com/en/Filament/CheatSheet), [TDS and SDS library](https://wiki.overture3d.com/en/Filament/TDS%26SDS) |
| Polymaker | 48 | 12 PolyLite, PolyTerra, and Fiberon families | Preserve built-in names and add the broader Panchroma, PolyMax, PolyFlex, engineering, and support ranges that fit Bambu hot-end limits. | [Filament guide](https://shop.polymaker.com/pages/filament-guide), [print profiles](https://polymaker.com/download-category/print-profiles-downloads/) |
| Prusament | 22 | None | Add PLA, PETG, ASA, PC, PA11, TPU, PP, filled, recycled, and specialty products. | [Material catalog](https://prusament.com/materials/) |
| MatterHackers | 16 | None | Add PRO, Build, NylonX, NylonG, Ryno, Quantum, and flexible products. | [PRO Series catalog](https://www.matterhackers.com/store/c/pro-series-filament) |
| Fiberlogy | 25 | None | Add aesthetic, engineering, flexible, composite, and support product families. | [Filament catalog](https://fiberlogy.com/en_US/c/Filaments/117/1/default/3), [printing and drying FAQ](https://fiberlogy.com/en/faq-2/) |
| Spectrum Filaments | 22 | None | Add PLA, PETG, ABS, ASA, PA, PC, TPU, composite, fire-retardant, and support products. | [Technical downloads](https://spectrumfilaments.com/en/download/) |
| Fillamentum | 20 | None | Add Extrafill, CPE, nylon, Flexfill, PP, Timberfill, Vinyl, and support products. | [Data sheets and printing guides](https://fillamentum.com/pages/data-sheets-and-3d-printing-guides/), [pocket printing guide](https://fillamentum.com/wp-content/uploads/2025/09/pocket-printing-guide.pdf) |

## Data Policy

- Temperatures, cooling, drying values, and enclosure or hardened-nozzle guidance come from official manufacturer product pages, technical sheets, or printing guides.
- Flow ratio and maximum volumetric speed are conservative starting values because manufacturers generally do not publish Bambu-specific calibration values for every product.
- Every setting remains editable in the importer after installation.
- Materials requiring more than a 300 C nozzle are omitted because they are outside the normal X1C/P1S hot-end range and would create unsafe or unusable presets for the original target hardware.
- New products and revised technical sheets should be updated in `manufacturers.json`, followed by package regeneration and validation.
