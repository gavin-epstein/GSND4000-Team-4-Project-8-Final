// resources used: https://docs.unity3d.com/Packages/com.unity.visualscripting@1.7/manual/vs-create-custom-node-add-relations.html
// https://en.wikipedia.org/wiki/Bresenham%27s_line_algorithm
// No i am not doing this in visual scripting. ever.

using NUnit;
using System;
using Unity.VisualScripting;
using UnityEngine;

public class BresenhamAlgorithm : Unit
{
    [DoNotSerialize]
    public ControlInput inputTrigger;

    [DoNotSerialize]
    public ControlOutput outputTrigger;

    [DoNotSerialize]
    public ValueInput Start;

    [DoNotSerialize]
    public ValueInput End;

    [DoNotSerialize]
    public ValueOutput result;

    private AotList resultValue;
    protected override void Definition()
    {
        //The lambda to execute our node action when the inputTrigger port is triggered.
        inputTrigger = ControlInput("inputTrigger", (flow) =>
        {
            
            resultValue = new AotList();
            int x0 = Mathf.RoundToInt(flow.GetValue<Vector2>(Start).x);
            int y0 = Mathf.RoundToInt(flow.GetValue<Vector2>(Start).y);
            int x1 = Mathf.RoundToInt(flow.GetValue<Vector2>(End).x);
            int y1 = Mathf.RoundToInt(flow.GetValue<Vector2>(End).y);

            if (Mathf.Abs(y1 - y0) < Mathf.Abs(x1 - x0)) {
                if (x0 > x1) {
                    plotLineQuadrant(x1, y1, x0, y0, resultValue, false);
                }
                else {
                    plotLineQuadrant(x0, y0, x1, y1, resultValue, false);
                }
            }
            else
            {
                if (y0 > y1) {
                    plotLineQuadrant(y1, x1, y0, x0, resultValue, true);
                }
                else
                {
                    plotLineQuadrant(y0, x0, y1, x1, resultValue, true);
                }
            }
            return outputTrigger;
        });
        outputTrigger = ControlOutput("outputTrigger");

        Start = ValueInput<Vector2>("Start",Vector2.zero );
        End = ValueInput<Vector2>("End", Vector2.zero);
        result = ValueOutput<AotList>("result", (flow) => resultValue);

        Requirement(Start, inputTrigger); //Specifies that we need the myValueA value to be set before the node can run.
        Requirement(End, inputTrigger); //Specifies that we need the myValueB value to be set before the node can run.
        Succession(inputTrigger, outputTrigger); //Specifies that the input trigger port's input exits at the output trigger port. Not setting your succession also dims connected nodes, but the execution still completes.

    }
    private void plotLineQuadrant(int x0, int y0, int x1, int y1, AotList result, Boolean high)
    {
        int dx = x1 - x0;
        int dy = y1 - y0;
        int yi = 1;
        if (dy < 0) {
            yi = -1;
            dy = -dy;
        }
        int D = (2 * dy) - dx;
        int y = y0;

        for (int x = x0; x <= x1; x++)
        {
            if (high) { // wikipedias "plotlinehigh" reverses x and y everywhere except this exact line
                result.Add(new Vector2(y, x));
            }
            else
            {
                result.Add(new Vector2(x, y));
            }
            if (D > 0)
            {
                y = y + yi;
                D = D + (2 * (dy - dx));
            }
            else
            {
                D = D + 2 * dy;
            }
        }
    }

}