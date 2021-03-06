﻿using MassSpectrometry;
using SharpLearning.Containers.Matrices;
using SharpLearning.CrossValidation.TrainingTestSplitters;
using SharpLearning.RandomForest.Learners;
using SharpLearning.RandomForest.Models;
using SharpLearning.Metrics.Regression;
using SharpLearning.Optimization;
using Spectra;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace EngineLayer.Calibration
{
    public class CalibrationEngine : MetaMorpheusEngine
    {
        #region Private Fields

        private const double maximumFracForTraining = 0.70;
        private const double maximumDatapointsToTrainWith = 20000;
        private const int trainingIterations = 30;

        private readonly IMsDataFile<IMsDataScan<IMzSpectrum<IMzPeak>>> myMsDataFile;
        private readonly DataPointAquisitionResults datapoints;

        #endregion Private Fields

        #region Public Constructors

        public CalibrationEngine(IMsDataFile<IMsDataScan<IMzSpectrum<IMzPeak>>> myMSDataFile, DataPointAquisitionResults datapoints, List<string> nestedIds) : base(nestedIds)
        {
            this.myMsDataFile = myMSDataFile;
            this.datapoints = datapoints;
        }

        #endregion Public Constructors

        #region Protected Methods

        protected override MetaMorpheusEngineResults RunSpecific()
        {
            double fracForTraining = maximumFracForTraining;

            var myMs1DataPoints = new List<(double[] xValues, double yValue)>();
            var myMs2DataPoints = new List<(double[] xValues, double yValue)>();
            
            // generate MS1 calibration datapoints
            Status("Generating MS1 calibration function");
            for (int i = 0; i < datapoints.Ms1List.Count; i++)
            {
                // x values
                var explanatoryVariables = new double[5];
                explanatoryVariables[0] = datapoints.Ms1List[i].mz;
                explanatoryVariables[1] = datapoints.Ms1List[i].rt;
                explanatoryVariables[2] = datapoints.Ms1List[i].logTotalIonCurrent;
                explanatoryVariables[3] = datapoints.Ms1List[i].logInjectionTime;
                explanatoryVariables[4] = datapoints.Ms1List[i].logIntensity;

                // y value
                double mzError = datapoints.Ms1List[i].mz - datapoints.Ms1List[i].expectedMZ;

                myMs1DataPoints.Add((explanatoryVariables, mzError));
            }

            if (myMs1DataPoints.Count * maximumFracForTraining > maximumDatapointsToTrainWith)
            {
                fracForTraining = maximumDatapointsToTrainWith / myMs1DataPoints.Count;
            }

            var ms1Model = GetRandomForestModel(myMs1DataPoints, fracForTraining);

            // generate MS2 calibration datapoints
            Status("Generating MS2 calibration function");
            for (int i = 0; i < datapoints.Ms2List.Count; i++)
            {
                // x values
                var explanatoryVariables = new double[5];
                explanatoryVariables[0] = datapoints.Ms2List[i].mz;
                explanatoryVariables[1] = datapoints.Ms2List[i].rt;
                explanatoryVariables[2] = datapoints.Ms2List[i].logTotalIonCurrent;
                explanatoryVariables[3] = datapoints.Ms2List[i].logInjectionTime;
                explanatoryVariables[4] = datapoints.Ms2List[i].logIntensity;

                // y value
                double mzError = datapoints.Ms2List[i].mz - datapoints.Ms2List[i].expectedMZ;

                myMs2DataPoints.Add((explanatoryVariables, mzError));
            }

            if (myMs2DataPoints.Count * maximumFracForTraining > maximumDatapointsToTrainWith)
            {
                fracForTraining = maximumDatapointsToTrainWith / myMs2DataPoints.Count;
            }

            var ms2Model = GetRandomForestModel(myMs2DataPoints, fracForTraining);
            
            Status("Calibrating spectra");

            CalibrateSpectra(ms1Model, ms2Model);

            return new MetaMorpheusEngineResults(this);
        }

        #endregion Protected Methods

        #region Private Methods
        
        private void CalibrateSpectra(RegressionForestModel ms1predictor, RegressionForestModel ms2predictor)
        {
            Parallel.ForEach(Partitioner.Create(1, myMsDataFile.NumSpectra + 1), fff =>
              {
                  for (int i = fff.Item1; i < fff.Item2; i++)
                  {
                      var scan = myMsDataFile.GetOneBasedScan(i);

                      if (scan is IMsDataScanWithPrecursor<IMzSpectrum<IMzPeak>> ms2Scan)
                      {
                          var precursorScan = myMsDataFile.GetOneBasedScan(ms2Scan.OneBasedPrecursorScanNumber.Value);

                          if (!ms2Scan.SelectedIonMonoisotopicGuessIntensity.HasValue && ms2Scan.SelectedIonMonoisotopicGuessMz.HasValue)
                          {
                              ms2Scan.ComputeMonoisotopicPeakIntensity(precursorScan.MassSpectrum);
                          }
                          
                          double theFunc(IPeak x) => x.X - ms2predictor.Predict(new double[] { x.X, scan.RetentionTime, Math.Log(scan.TotalIonCurrent), scan.InjectionTime.HasValue ? Math.Log(scan.InjectionTime.Value) : double.NaN, Math.Log(x.Y) });

                          double theFuncForPrecursor(IPeak x) => x.X - ms1predictor.Predict(new double[] { x.X, precursorScan.RetentionTime, Math.Log(precursorScan.TotalIonCurrent), precursorScan.InjectionTime.HasValue ? Math.Log(precursorScan.InjectionTime.Value) : double.NaN, Math.Log(x.Y) });

                          ms2Scan.TransformMzs(theFunc, theFuncForPrecursor);
                      }
                      else
                      {
                          Func<IPeak, double> theFunc = x => x.X - ms1predictor.Predict(new double[] { x.X, scan.RetentionTime, Math.Log(scan.TotalIonCurrent), scan.InjectionTime.HasValue ? Math.Log(scan.InjectionTime.Value) : double.NaN, Math.Log(x.Y) });
                          scan.MassSpectrum.ReplaceXbyApplyingFunction(theFunc);
                      }
                  }
              }
              );
        }

        private RegressionForestModel GetRandomForestModel(List<(double[] xValues, double yValue)> myInputs, double fracForTraining, int randomSeed = 42)
        {
            // create a machine learner
            var learner = new RegressionRandomForestLearner();
            var metric = new MeanSquaredErrorRegressionMetric();

            var splitter = new RandomTrainingTestIndexSplitter<double>(trainingPercentage: fracForTraining, seed: randomSeed);

            // put x values into a matrix and y values into a 1D array
            var myXValueMatrix = new F64Matrix(myInputs.Count, myInputs.First().xValues.Length);
            for (int i = 0; i < myInputs.Count; i++)
                for (int j = 0; j < myInputs.First().xValues.Length; j++)
                    myXValueMatrix[i, j] = myInputs[i].xValues[j];

            var myYValues = myInputs.Select(p => p.yValue).ToArray();

            // split data into training set and test set
            var splitData = splitter.SplitSet(myXValueMatrix, myYValues);
            var trainingSetX = splitData.TrainingSet.Observations;
            var trainingSetY = splitData.TrainingSet.Targets;

            // learn an initial model
            var myModel = learner.Learn(trainingSetX, trainingSetY);

            // parameter ranges for the optimizer 
            var parameters = new ParameterBounds[]
            {
                new ParameterBounds(min: 100, max: 150, transform: Transform.Linear),
                new ParameterBounds(min: 1, max: 5, transform: Transform.Linear),
                new ParameterBounds(min: 500, max: 2000, transform: Transform.Linear),
                new ParameterBounds(min: 0, max: 2, transform: Transform.Linear),
                new ParameterBounds(min: 1e-06, max: 1e-05, transform: Transform.Logarithmic),
                new ParameterBounds(min: 0.7, max: 1.5, transform: Transform.Linear)
            };

            var validationSplit = new RandomTrainingTestIndexSplitter<double>(trainingPercentage: fracForTraining, seed: randomSeed)
                .SplitSet(myXValueMatrix, myYValues);

            // define minimization metric
            Func<double[], OptimizerResult> minimize = p =>
            {
                // create the candidate learner using the current optimization parameters
                var candidateLearner = new RegressionRandomForestLearner(
                    trees: (int)p[0],
                    minimumSplitSize: (int)p[1],
                    maximumTreeDepth: (int)p[2],
                    featuresPrSplit: (int)p[3],
                    minimumInformationGain: p[4],
                    subSampleRatio: p[5],
                    seed: randomSeed,
                    runParallel: false);

                var candidateModel = candidateLearner.Learn(validationSplit.TrainingSet.Observations,
                validationSplit.TrainingSet.Targets);

                var validationPredictions = candidateModel.Predict(validationSplit.TestSet.Observations);
                var candidateError = metric.Error(validationSplit.TestSet.Targets, validationPredictions);

                return new OptimizerResult(p, candidateError);
            };

            // create optimizer
            var optimizer = new RandomSearchOptimizer(parameters, iterations: trainingIterations, runParallel: true);

            // find best parameters
            var result = optimizer.OptimizeBest(minimize);
            var best = result.ParameterSet;

            // create the final learner using the best parameters 
            // (parameters that resulted in the model with the least error)
            learner = new RegressionRandomForestLearner(
                    trees: (int)best[0],
                    minimumSplitSize: (int)best[1],
                    maximumTreeDepth: (int)best[2],
                    featuresPrSplit: (int)best[3],
                    minimumInformationGain: best[4],
                    subSampleRatio: best[5],
                    seed: randomSeed,
                    runParallel: true);

            // learn final model with optimized parameters
            myModel = learner.Learn(trainingSetX, trainingSetY);

            // all done
            return myModel;
        }

        #endregion Private Methods
    }
}