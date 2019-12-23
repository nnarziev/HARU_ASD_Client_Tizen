﻿namespace HARU_ASD.Model
{
    public class LightModel : BaseSensorModel
    {
        private float level;

        public float Level
        {
            get { return level; }
            set
            {
                level = value;
                OnPropertyChanged();
            }
        }
    }
}