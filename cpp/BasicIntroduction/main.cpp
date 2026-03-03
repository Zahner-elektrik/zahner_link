#include <zahnerlinkexc.h>
#include <helpers/hardwaresettingshelper.h>
#include <jobs/control/jobs/switch_off/switch_offjob.h>
#include <jobs/control/jobs/switch_on/switch_onjob.h>
#include <jobs/calibration/jobs/calibration/calibratejob.h>
#include <jobs/dc/jobs/poga/pogajob.h>
#include <jobs/ac/jobs/eis/eisjob.h>
#include <xml/measurement.h>
#include <xml/zxmlexporter.h>
#include <iostream>
#include <memory>

/**
 * @brief Getting Started with the zahner_link C++ Library
 *
 * This tutorial serves as your first step in learning how to use the zahner_link C++ library
 * to control Zahner IM7 series potentiostats.
 *
 * Error handling is ignored in this example; this will be covered in a separate example.
 * The exception-based library is also used for clearer examples.
 *
 * It covers:
 * - Connecting to the device
 * - Performing calibration
 * - Basic Potentiostatic/Galvanostatic (POGA) measurements
 * - Electrochemical Impedance Spectroscopy (EIS)
 * - Data handling (saving and combining datasets)
 * - Proper shutdown procedure
 */

int main(int argc, char *argv[])
{
    // =============================================================================================
    // 1. Connecting to the IM7
    // =============================================================================================

    // Ensure your IM7 device is powered on and fully booted before connecting.
    // Create a ZahnerLinkExc object using the IM7's IP address (here "169.254.9.137")
    // and port number.
    // As an alternative, there is also the non-exception-based version of the ZahnerLink class.
    ZahnerLinkExc link("169.254.9.137", "1994");

    auto status = link.connect();

    if (status != ZahnerLinkServiceStatusEnum::SUCCESS_NO_ERROR)
    {
        std::cout << "Failed to connect to IM7. Status: " << static_cast<int>(status) << std::endl;
        return 1;
    }
    std::cout << "Successfully connected to IM7." << std::endl;

    // =============================================================================================
    // 2. Performing DC Calibration
    // =============================================================================================

    // After your IM7 has been warming up for at least 30 minutes, you should perform a DC calibration.
    // This ensures accurate measurements.
    std::cout << "Configuring DC Calibration..." << std::endl;
    CalibrateJob dcCalibrationJob({.calibration_type = CalibrationTypesEnum::DC});

    // Note: Calibration takes several minutes.
    // It is commented out here for quick execution of the example, but should be uncommented for real tests.
    // link.doJob(dcCalibrationJob);
    std::cout << "Skipped DC Calibration (uncomment in code to run)." << std::endl;

    // =============================================================================================
    // 3. Switching On the Potentiostat
    // =============================================================================================

    // Before taking any measurements, we need to switch on the potentiostat.
    // We set the initial state (potentiostatic 1.0 V).
    std::cout << "Switching On the Potentiostat..." << std::endl;
    SwitchOnJob switchOnJob({.potentiostat = "MAIN:1:POT",
                             .coupling = PotentiostatCoupling::POTENTIOSTATIC,
                             .bias = 1.0, // Start at 1.0 V
                             .voltage_range_index = 0,
                             .compliance_range_index = 0});
    link.doJob(switchOnJob);
    std::cout << "Potentiostat is ON." << std::endl;

    // =============================================================================================
    // 4. Running Measurements
    // =============================================================================================

    // ---------------------------------------------------------------------------------------------
    // Part A: Potentiostatic Polarization (DC)
    // ---------------------------------------------------------------------------------------------

    // We configure a PogaJob (Potentiostatic/Galvanostatic) for a 5-second measurement at 1.0 V.
    std::cout << "Starting first DC polarization measurement (1.0 V)..." << std::endl;
    PogaJob potentiostaticPolarizationJob({.bias = 1.0,
                                           .duration = 5.0,
                                           .output_data_rate = 25.0,
                                           .autorange = true,
                                           .current_range = 0.1,
                                           .ir_drop = 0.0});
    link.doJob(potentiostaticPolarizationJob);

    // Retrieve the data from the first run from the IM7.
    // getJobResultDataT returns a smart pointer to the dataset.
    auto dcDataset1 = link.getJobResultDataT(potentiostaticPolarizationJob);
    std::cout << "First DC measurement finished." << std::endl;

    // Reuse the job object for a second measurement, changing the bias to -1.0 V.
    std::cout << "Starting second DC polarization measurement (-1.0 V)..." << std::endl;
    potentiostaticPolarizationJob.parameters.bias = -1.0;
    link.doJob(potentiostaticPolarizationJob);

    auto dcDataset2 = link.getJobResultDataT(potentiostaticPolarizationJob);
    std::cout << "Second DC measurement finished." << std::endl;

    // Combine the two datasets into one.
    std::cout << "Combining DC datasets..." << std::endl;
    dcDataset1->append(dcDataset2);

    // Create a Measurement object from the combined dataset for export.
    ZXml::Measurement xmlMeasurement(dcDataset1);

    // Save to file.
    ZXml::ZXmlExporter exporter;
    exporter.setCompactXml(false); // logical formatting for readability
    exporter.saveAsFileStandalone(xmlMeasurement, "polarization.zmx");
    std::cout << "Saved combined DC data to 'polarization.zmx'." << std::endl;

    // ---------------------------------------------------------------------------------------------
    // Part B: Electrochemical Impedance Spectroscopy (EIS)
    // ---------------------------------------------------------------------------------------------

    // Configure an EIS sweep from 1 kHz to 10 kHz downto 100 Hz.
    // Using designated initializers for clear parameter naming.
    std::cout << "Starting EIS Generate Job (Frequency Sweep)..." << std::endl;
    EisGenerateJob eisGenerateJob(
        {.bias = 0,
         .min_frequency = 100,
         .max_frequency = 1e4,
         .start_frequency = 1e3,
         .points_per_decade_upper = 20,
         .points_per_decade_lower = 6,
         .pre_duration = 0.1,
         .pre_waves = 1,
         .meas_duration = 0.2,
         .meas_waves = 3,
         .amplitude = 0.01});
    link.doJob(eisGenerateJob);

    // Retrieve and save the first EIS result.
    auto eisDataset1 = link.getJobResultDataT(eisGenerateJob);
    exporter.saveAsFileStandalone(ZXml::Measurement(eisDataset1), "eis_generate.zmx");
    std::cout << "Saved EIS sweep data to 'eis_generate.zmx'." << std::endl;

    // ---------------------------------------------------------------------------------------------
    // Part C: EIS Frequency Table
    // ---------------------------------------------------------------------------------------------

    // Configure an EIS measurement with specific frequency points (1k Hz, 20 k Hz) defined in a table.
    std::cout << "Starting EIS Frequency Table Job..." << std::endl;
    EisFrequencyTableJob eisTableJob(
        {.bias = 0.0,
         .spectrum = {
             {.frequency = 1000,
              .amplitude = 0.01,
              .pre_duration = 0.1,
              .pre_waves = 1,
              .meas_duration = 0.3,
              .meas_waves = 3},
             {.frequency = 20e3,
              .amplitude = 0.1, // Difference amplitude for this point
              .pre_duration = 0.1,
              .pre_waves = 1,
              .meas_duration = 0.2,
              .meas_waves = 3},
         }});
    link.doJob(eisTableJob);

    // Retrieve and save the second EIS result.
    auto eisDataset2 = link.getJobResultDataT(eisTableJob);
    exporter.saveAsFileStandalone(ZXml::Measurement(eisDataset2), "eis_table.zmx");
    std::cout << "Saved EIS table data to 'eis_table.zmx'." << std::endl;

    // Combine both EIS datasets.
    std::cout << "Combining EIS datasets..." << std::endl;
    eisDataset1->append(eisDataset2);
    exporter.saveAsFileStandalone(ZXml::Measurement(eisDataset1), "eis_combined.zmx");
    std::cout << "Saved combined EIS data to 'eis_combined.zmx'." << std::endl;

    // =============================================================================================
    // 5. Switching Off and Disconnecting
    // =============================================================================================

    // Always switch off the potentiostat when finished.
    std::cout << "Switching Off the Potentiostat..." << std::endl;
    SwitchOffJob switchOffJob({.potentiostat = "MAIN:1:POT"});
    link.doJob(switchOffJob);

    // Disconnect cleanly from the service.
    // After disconnecting, existing data objects (like datasets) are still valid,
    // but no new jobs can be sent.
    link.disconnect();
    std::cout << "Disconnected from IM7." << std::endl;

    std::cout << "Tutorial finished successfully." << std::endl;
    return 0;
}
