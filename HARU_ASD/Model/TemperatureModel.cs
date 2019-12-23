namespace HARU_ASD.Model
{
    public class TemperatureModel : BaseSensorModel
    {
        private float temperature;

        public float Temperature
        {
            get { return temperature; }
            set
            {
                temperature = value;
                OnPropertyChanged();
            }
        }
    }
}