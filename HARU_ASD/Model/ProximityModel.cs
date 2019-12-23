namespace HARU_ASD.Model
{
    public class ProximityModel : BaseSensorModel
    {
        private string proximity;

        public string Proximity
        {
            get { return proximity; }
            set
            {
                proximity = value;
                OnPropertyChanged();
            }
        }
    }
}