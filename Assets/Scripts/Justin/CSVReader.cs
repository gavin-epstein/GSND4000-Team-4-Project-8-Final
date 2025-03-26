using UnityEngine;
using System;
using System.Collections.Generic;
using System.IO;
using Unity.VisualScripting;

[Inspectable]
public struct BulletSpawnData
{
    public Vector2 position;
    public int direction; // 0 = up, 1 = left, 2 = down, 3 = right
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
        position = new Vector2(intValues[0], intValues[1]);
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
    public ValueInput csvFile;

    [DoNotSerialize]
    public ValueOutput bulletSpawnData;

    private List<BulletSpawnData> outputData;

    protected override void Definition()
    {
        inputTrigger = ControlInput(
            "ReadCSV",
            (flow) =>
            {
                outputData = ReadCSV(flow.GetValue<TextAsset>(csvFile));
                return outputTrigger;
            }
        );
        outputTrigger = ControlOutput("Output");

        csvFile = ValueInput<TextAsset>("csvTextAsset");
        bulletSpawnData = ValueOutput<List<BulletSpawnData>>(
            "bulletSpawnData",
            (flow) => outputData
        );

        Requirement(csvFile, inputTrigger);
        Succession(inputTrigger, outputTrigger);
        Assignment(inputTrigger, bulletSpawnData);
    }

    private List<BulletSpawnData> ReadCSV(TextAsset csvFile)
    {
        if (csvFile == null)
        {
            Debug.LogError("No CSV file provided");
            return null;
        }
        StringReader reader = new StringReader(csvFile.text);
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
