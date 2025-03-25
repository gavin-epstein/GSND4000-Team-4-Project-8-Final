using UnityEngine;
using System;
using System.Collections.Generic;
using System.IO;
using Unity.VisualScripting;

[Inspectable]
public struct BulletSpawnData
{
    public int x;
    public int y;
    public int direction; // 1 = up, 2 = right, 3 = down, 4 = left
    public int turn; // the turn the bullet spawns on

    public BulletSpawnData(string[] values)
    {
        if (values.Length != 4)
        {
            throw new ArgumentException("BulletSpawnData requires 4 values");
        }
        int[] intValues = new int[4];
        for (int i = 0; i < 4; i++)
        {
            if (!int.TryParse(values[i], out intValues[i]))
            {
                throw new ArgumentException(
                    "Failed to parse value " + values[i] + " as an integer"
                );
            }
        }
        x = intValues[0];
        y = intValues[1];
        direction = intValues[2];
        turn = intValues[3];
    }
}

public class CSVReader : Unit
{
    [DoNotSerialize]
    public ControlInput inputTrigger;

    [DoNotSerialize]
    public ControlOutput outputTrigger;

    [DoNotSerialize]
    public ValueInput filePath;

    [DoNotSerialize]
    public ValueOutput bulletSpawnData;

    private List<BulletSpawnData> outputData;

    protected override void Definition()
    {
        inputTrigger = ControlInput(
            "ReadCSV",
            (flow) =>
            {
                outputData = ReadCSV(flow.GetValue<string>(filePath));
                return outputTrigger;
            }
        );
        outputTrigger = ControlOutput("Output");

        filePath = ValueInput<string>("relativeFilePath", "Assets/Resources/Level1.csv");
        bulletSpawnData = ValueOutput<List<BulletSpawnData>>(
            "bulletSpawnData",
            (flow) => outputData
        );

        Requirement(filePath, inputTrigger);
        Succession(inputTrigger, outputTrigger);
        Assignment(inputTrigger, bulletSpawnData);
    }

    private List<BulletSpawnData> ReadCSV(string filePathString)
    {
        StreamReader reader = new StreamReader(filePathString);
        // Read the header
        string[] header = reader.ReadLine().Split(',');
        if (header.Length != 4)
        {
            Debug.LogError("CSV file is not formatted correctly");
            return null;
        }
        bool eof = false;
        List<BulletSpawnData> output = new List<BulletSpawnData>();
        while (!eof)
        {
            string line = reader.ReadLine();
            if (line == null)
            {
                eof = true;
                break;
            }
            string[] values = line.Split(',');
            if (values.Length != header.Length)
            {
                Debug.LogError("CSV file is not formatted correctly");
                return null;
            }
            output.Add(new BulletSpawnData(values));
        }
        return output;
    }
}
