# zahner_link

[![PyPI version](https://img.shields.io/pypi/v/zahner_link)](https://pypi.org/project/zahner_link/)
[![Python versions](https://img.shields.io/pypi/pyversions/zahner_link)](https://pypi.org/project/zahner_link/)
[![Documentation](https://img.shields.io/badge/docs-doc.zahner.de-blue)](https://doc.zahner.de/im7/apis/zahner_link)
[![License](https://img.shields.io/badge/license-Zahner%20Software%20License-blue)](LICENSE)

The zahner_link library provides comprehensive programmatic control of the Zahner IM7/c/x Electrochemical Workstations. Available for both **Python** and **C++**, this job-based library enables you to automate complex measurement protocols and integrate electrochemical experiments into your data analysis workflows.

What makes this library particularly powerful is that it uses the exact same C++ core library as Zahner Lab itself. This means you get identical functionality whether you're working in the GUI or writing code, no feature compromises or compatibility issues to worry about.

# 📚 Documentation

The complete documentation is available on the [API documentation website](https://doc.zahner.de/im7/apis/zahner_link), including:

- A detailed reference for every job, class, and function
- Dedicated sections for the [Python](https://doc.zahner.de/im7/apis/zahner_link/python/) and C++ APIs
- The [examples](https://doc.zahner.de/im7/apis/zahner_link/python/index.html#examples) from this repository, rendered for online browsing

# ✨ Features

- **Job-based API** - build measurements from composable jobs instead of low-level commands
- **Identical core to Zahner Lab** - uses the exact same C++ core library as the GUI, so results and capabilities match
- **Python and C++** - the same API available as a Python package and a C++ library
- **Cross-platform** - pre-built wheels for Windows, Linux (glibc/musl), and macOS

### Measurement Techniques

- Electrochemical Impedance Spectroscopy (auto-generated or custom frequency tables)
- Potentiostatic and galvanostatic polarization
- Pulse voltammetry (NPV, DPV, SWV)
- Current-voltage curves (e.g. Cyclic Voltammetry)
- Measurements with arbitrary, freely defined excitation signals

### Data and Control

- Live DC and EIS data via callbacks
- Stop conditions to extend measurement primitives
- Access to raw EIS waveform data
- Export to Zahner XML (`.zmx`), directly openable in [Zahner Analysis](https://zahner.de/products-details/software/zahner-analysis)
- Exception-based and return-value-based API variants with connection-loss recovery

### Supported Hardware

- IM7/c/x Electrochemical Workstations
- Extension cards: PAD42, TEMP-U2, RMUX16, MIO, EPC42

# 🔧 Installation

The package can be installed via pip.

```bash
pip install zahner_link
```

# ✅ Requirements

- Python 3.11 or later

Pre-built wheels are available for:

- Windows: x86_64
- Linux: x86_64 and ARM64 (glibc and musl)
- macOS: ARM64

# 🔨 Basic Usage

The [Jupyter](https://jupyter.org/) notebook [BasicIntroduction.ipynb](python/BasicIntroduction/BasicIntroduction.ipynb) explains the fundamentals of using the library. The equivalent C++ example is available in [cpp/BasicIntroduction/main.cpp](cpp/BasicIntroduction/main.cpp).

```python
import zahner_link as zl

"""
Connect to the Zahner IM7 via its IP address and port.
"""
link = zl.ZahnerLinkExc("10.10.253.150", "1994")
link.connect()

"""
Switch on the main potentiostat in potentiostatic mode at 1 V DC.
All values use SI base units (volts, amperes, seconds).
"""
switch_on_job = zl.control.SwitchOnJob(
    potentiostat="MAIN:1:POT",
    coupling=zl.PotentiostatCoupling.POTENTIOSTATIC,
    bias=1.0,
    voltage_range_index=0,
    compliance_range_index=0,
)
link.do_job(switch_on_job)

"""
EIS measurement at 1 V DC with 10 mV amplitude.
The frequency points are generated automatically from start_frequency
up to max_frequency, then down to min_frequency.
"""
eis_generate_job = zl.meas.EisGenerateJob(
    bias=1.0,
    min_frequency=10,
    max_frequency=1e5,
    start_frequency=10e3,
    points_per_decade_upper=8,
    points_per_decade_lower=5,
    pre_duration=0,
    pre_waves=1,
    meas_duration=0.1,
    meas_waves=4,
    amplitude=10e-3,
)
link.do_job(eis_generate_job)

"""
Read out the measured frequency and complex impedance.
"""
eis_generate_data = link.get_job_result_data(eis_generate_job)
print("Frequency: " + str(eis_generate_data.get_frequencies()))
print("Impedance: " + str(eis_generate_data.get_impedance_data().get_calculated_complex_impedance_track()))

"""
Export the measurement to Zahner XML format, which can be opened in Zahner Analysis.
"""
xml_measurement = zl.xml.Measurement(eis_generate_data)
exporter = zl.xml.ZXmlExporter()
exporter.set_compact_xml(False)
exporter.save_as_file_standalone(xml_measurement, "eis_generate_job.zmx")

"""
Switch off the potentiostat and disconnect.
"""
link.do_job(zl.control.SwitchOffJob(potentiostat="MAIN:1:POT"))
link.disconnect()
```

# 📖 Examples

The examples are part of this repository as [Jupyter](https://jupyter.org/) notebooks under [python/](python/) and are also rendered in the [documentation](https://doc.zahner.de/im7/apis/zahner_link/python/index.html#examples).

| Example | Description |
| --- | --- |
| [BasicIntroduction](python/BasicIntroduction/BasicIntroduction.ipynb) | First steps: connecting, DC calibration, switching on, a simple measurement and data export |
| [Polarizations](python/Polarizations/Polarizations.ipynb) | Potentiostatic and galvanostatic polarization measurements and data handling |
| [Eis](python/Eis/Eis.ipynb) | Electrochemical Impedance Spectroscopy with generated and custom frequency tables |
| [PulseVoltammetry](python/PulseVoltammetry/PulseVoltammetry.ipynb) | Pulse voltammetry techniques (NPV, DPV, SWV) |
| [CurrentVoltageCurves](python/CurrentVoltageCurves/CurrentVoltageCurves.ipynb) | Recording current-voltage curves such as cyclic voltammetry |
| [CurrentDependentCharacterization](python/CurrentDependentCharacterization/CurrentDependentCharacterization.ipynb) | Characterizing a device as a function of the DC bias current |
| [ArbitrarySignal](python/ArbitrarySignal/ArbitrarySignal.ipynb) | Measurements with arbitrary, freely defined excitation signals |
| [EisWaves](python/EisWaves/EisWaves.ipynb) | Accessing and visualizing the raw EIS waveform data |
| [StopConditions](python/StopConditions/StopConditions.ipynb) | Extending measurement primitives with stop conditions |
| [LiveDataCallbacks](python/LiveDataCallbacks/LiveDataCallbacks.ipynb) | Receiving live DC and EIS data through callbacks |
| [ChannelConfiguration](python/ChannelConfiguration/ChannelConfiguration.ipynb) | Advanced channel configuration, including PAD4 cards |
| [TempuRmuxMio](python/TempuRmuxMio/TempuRmuxMio.ipynb) | Using the TEMP-U2, RMUX16, and MIO extension cards |
| [ErrorHandling](python/ErrorHandling/ErrorHandling.ipynb) | Error handling, connection loss recovery, and job inspection |

# 📧 Having a question?

Send a [mail](mailto:support@zahner.de?subject=zahner_link%20Question&body=Your%20Message) to our support team.

# ⁉️ Found a bug or missing a specific feature?

Feel free to **create a new issue** with an appropriate title and description in the [zahner_link repository issue tracker](https://github.com/Zahner-elektrik/zahner_link/issues). Or send a [mail](mailto:support@zahner.de?subject=zahner_link%20Question&body=Your%20Message) to our support team.

# ⚖️ License

The `zahner_link` library is licensed under the Zahner Software License.

This project also includes third-party software components. The licenses for these components can be found in the `zahner_link/third_party_licenses` directory of the installed package.
